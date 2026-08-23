using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;

namespace MyAssistant.Providers;

public class OpenAICompatibleProvider : IStreamingProvider
{
    private static readonly HttpClient SharedHttp = CreateHttp();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly bool _isGemini;

    public OpenAICompatibleProvider(string baseUrl, string? apiKey = null)
    {
        _apiKey = apiKey;
        _isGemini = (baseUrl ?? "").Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
        if (_isGemini)
        {
            // Normalize Gemini URL to OpenAI-compatible endpoint
            var lower = baseUrl!.ToLowerInvariant();
            if (!lower.Contains("/openai"))
                _baseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";
            else
                _baseUrl = baseUrl!.TrimEnd('/');
        }
        else
        {
            _baseUrl = (baseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        }
    }

    public string ProviderId => "openai-compatible";

    public async IAsyncEnumerable<StreamDelta> StreamAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/chat/completions";
        var body = BuildRequest(request, stream: true);
        var startedToolCalls = new HashSet<int>();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(900));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        string? initialError = null;
        HttpResponseMessage? response = null;

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(_apiKey))
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            httpRequest.Headers.Add("HTTP-Referer", "https://github.com/myassistant");
            httpRequest.Headers.Add("X-Title", "MyAssistant");
            response = await SharedHttp.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        { initialError = $"Request timed out (900s). Check provider URL: {_baseUrl}"; }
        catch (HttpRequestException ex)
        { initialError = $"Cannot connect to {_baseUrl}: {ex.Message}"; }
        catch (Exception ex)
        { initialError = $"Request failed: {ex.Message}"; }

        if (initialError != null)
        {
            yield return new StreamDelta.Error(initialError);
            yield break;
        }

        using (response)
        {
            if (!response!.IsSuccessStatusCode)
            {
                string errorBody = "(could not read response)";
                try { errorBody = await response.Content.ReadAsStringAsync(linkedCts.Token); } catch { }
                yield return new StreamDelta.Error($"Provider returned {(int)response.StatusCode}: {Truncate(errorBody, 500)}");
                yield break;
            }

            Stream? stream = null;
            string? streamError = null;
            try { stream = await response.Content.ReadAsStreamAsync(linkedCts.Token); }
            catch (Exception ex) { streamError = $"Stream error: {ex.Message}"; }

            if (streamError != null) { yield return new StreamDelta.Error(streamError); yield break; }

            using (stream!)
            using (var reader = new StreamReader(stream!, Encoding.UTF8))
            {
                var receivedAnyData = false;
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    string? line = null;
                    string? readError = null;
                    bool readFailed = false;

                    try
                    {
                        // Fast-fail if the provider never streams anything at all;
                        // tolerate longer gaps once generation has started
                        var idleBudget = receivedAnyData ? TimeSpan.FromSeconds(300) : TimeSpan.FromSeconds(60);
                        using var readTimeout = new CancellationTokenSource(idleBudget);
                        using var readLinked = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, readTimeout.Token);
                        line = await reader.ReadLineAsync(readLinked.Token);
                        if (line != null) receivedAnyData = true;
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    { readError = "Request timed out (900s)."; readFailed = true; }
                    catch (OperationCanceledException) when (!receivedAnyData)
                    { readError = $"No data received within 60s ({_baseUrl}). The provider gateway may be stalling — retry or switch provider."; readFailed = true; }
                    catch (OperationCanceledException)
                    { readError = "No data received for 300s."; readFailed = true; }
                    catch (Exception ex)
                    { readError = $"Stream read error: {ex.Message}"; readFailed = true; }

                    if (readFailed) { yield return new StreamDelta.Error(readError!); yield break; }
                    if (line is null) break;
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                    var payload = line[5..].Trim();
                    if (payload == "[DONE]") break;

                    OpenAIStreamChunk? chunk;
                    try { chunk = JsonSerializer.Deserialize<OpenAIStreamChunk>(payload); }
                    catch { continue; }

                    if (chunk?.Choices == null) continue;

                    foreach (var choice in chunk.Choices)
                    {
                        if (choice.Delta?.ToolCalls != null)
                        {
                            foreach (var tc in choice.Delta.ToolCalls)
                            {
                                var idx = tc.Index ?? startedToolCalls.Count;
                                var thoughtSig = ExtractThoughtSignature(tc.ExtraContent);
                                if (!startedToolCalls.Contains(idx) && !string.IsNullOrEmpty(tc.Function?.Name))
                                {
                                    startedToolCalls.Add(idx);
                                    var id = string.IsNullOrEmpty(tc.Id) ? $"call_{idx}_{Guid.NewGuid():N}" : tc.Id!;
                                    yield return new StreamDelta.ToolCallStart(id, tc.Function.Name!, thoughtSig);
                                }
                                if (!string.IsNullOrEmpty(tc.Function?.Arguments))
                                    yield return new StreamDelta.ToolCallArgumentDelta(tc.Function.Arguments);
                            }
                        }

                        if (!string.IsNullOrEmpty(choice.Delta?.Content))
                            yield return new StreamDelta.TextContent(choice.Delta.Content);

                        if (!string.IsNullOrEmpty(choice.FinishReason) && choice.FinishReason != "tool_calls")
                            yield break;
                    }

                    if (chunk.Usage != null)
                        yield return new StreamDelta.UsageDelta(
                            chunk.Usage.PromptTokens,
                            chunk.Usage.CompletionTokens,
                            chunk.Usage.TotalTokens);
                }
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await RetryHelper.ExecuteAsync(async () =>
            {
                var url = $"{_baseUrl}/models";
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(_apiKey))
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                using var response = await SharedHttp.SendAsync(httpRequest, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return (IReadOnlyList<string>)Array.Empty<string>();

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                if (json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    return data.EnumerateArray()
                        .Where(m => m.TryGetProperty("id", out _))
                        .Select(m => { var id = m.GetProperty("id").GetString() ?? ""; return id.StartsWith("models/") ? id["models/".Length..] : id; })
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToList();
                }
                return (IReadOnlyList<string>)Array.Empty<string>();
            }, maxRetries: 2, initialDelay: TimeSpan.FromSeconds(0.5),
            shouldRetry: ex => ex is HttpRequestException,
            cancellationToken: cancellationToken);
        }
        catch { return []; }
    }

    private OpenAIChatRequest BuildRequest(ProviderRequest request, bool stream)
    {
        var messages = request.Messages.Select(m => new OpenAIMessage(
            Role: m.Role.ToLowerInvariant(),
            Content: m.Content,
            ToolCalls: m.ToolCalls?.Select(tc =>
            {
                JsonElement? extraContent = null;
                if (!string.IsNullOrEmpty(tc.ThoughtSignature))
                {
                    var googleObj = JsonSerializer.SerializeToElement(new { thought_signature = tc.ThoughtSignature });
                    extraContent = JsonSerializer.SerializeToElement(new { google = googleObj });
                }
                return new OpenAIToolCall(
                    Id: tc.Id, Index: null, Type: "function",
                    Function: new OpenAIToolCallFunction(tc.Name, tc.Arguments),
                    ExtraContent: extraContent);
            }).ToList(),
            ToolCallId: m.ToolCallId)).ToList();

        var tools = request.Tools.Count > 0
            ? request.Tools.Select(t => new OpenAITool(
                Function: new OpenAIFunction(t.Name, t.Description,
                    JsonSerializer.Deserialize<JsonElement>(t.ParametersJson)))).ToList()
            : null;

        return new OpenAIChatRequest(
            Model: request.ModelId,
            Messages: messages,
            Stream: stream,
            Tools: tools,
            Temperature: request.Temperature,
            TopP: request.TopP,
            MaxTokens: request.MaxTokens,
            StreamOptions: stream ? new OpenAIStreamOptions(true) : null);
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(16) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyCoder/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string? ExtractThoughtSignature(JsonElement? extraContent)
    {
        if (extraContent is not { } ec) return null;
        try
        {
            if (ec.TryGetProperty("google", out var google) &&
                google.TryGetProperty("thought_signature", out var sig))
                return sig.GetString();
        }
        catch { }
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
