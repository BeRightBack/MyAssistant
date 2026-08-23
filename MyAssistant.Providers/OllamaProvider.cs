using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;

namespace MyAssistant.Providers;

public class OllamaProvider : IStreamingProvider
{
    private static readonly HttpClient SharedHttp = CreateHttp();
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    public OllamaProvider(string baseUrl = "http://localhost:11434", string? apiKey = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public string ProviderId => "ollama";

    public async IAsyncEnumerable<StreamDelta> StreamAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/api/chat";
        var body = BuildRequest(request, stream: true);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(900));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        string? initialError = null;
        HttpResponseMessage? response = null;

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(_apiKey))
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            response = await SharedHttp.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        { initialError = $"Connection to Ollama timed out (900s). Is Ollama running on {_baseUrl}?"; }
        catch (HttpRequestException ex)
        { initialError = $"Cannot connect to Ollama at {_baseUrl}: {ex.Message}"; }
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
                yield return new StreamDelta.Error($"Ollama returned {(int)response.StatusCode}: {Truncate(errorBody, 500)}");
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
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    string? line = null;
                    string? readError = null;
                    bool readFailed = false;

                    try
                    {
                        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(900));
                        using var readLinked = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, readTimeout.Token);
                        line = await reader.ReadLineAsync(readLinked.Token);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                    { readError = "Ollama request timed out (900s)."; readFailed = true; }
                    catch (OperationCanceledException)
                    { readError = "Read timed out (60s) waiting for Ollama response."; readFailed = true; }
                    catch (Exception ex)
                    { readError = $"Stream read error: {ex.Message}"; readFailed = true; }

                    if (readFailed) { yield return new StreamDelta.Error(readError!); yield break; }
                    if (line is null) break;
                    if (string.IsNullOrEmpty(line)) continue;

                    OllamaChatResponse? chunk;
                    try { chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line); }
                    catch { continue; }

                    if (chunk?.Message?.Thinking != null && chunk.Message.Thinking.Length > 0)
                        yield return new StreamDelta.ThinkingContent(chunk.Message.Thinking);

                    if (chunk?.Message?.Content != null && chunk.Message.Content.Length > 0)
                        yield return new StreamDelta.TextContent(chunk.Message.Content);

                    if (chunk?.Done == true && chunk.Message?.Thinking == null)
                        System.Diagnostics.Debug.WriteLine($"[Ollama] No thinking field in response for model: {chunk?.Model}");

                    if (chunk?.Message?.ToolCalls != null)
                    {
                        foreach (var tc in chunk.Message.ToolCalls)
                        {
                            var id = $"call_{Guid.NewGuid():N}";
                            yield return new StreamDelta.ToolCallStart(id, tc.Function.Name);
                            var argsStr = tc.Function.Arguments.ValueKind != JsonValueKind.Undefined
                                ? JsonSerializer.Serialize(tc.Function.Arguments)
                                : "";
                            if (argsStr.Length > 0)
                                yield return new StreamDelta.ToolCallArgumentDelta(argsStr);
                        }
                    }

                    if (chunk?.Done == true)
                    {
                        if (chunk.PromptEvalCount > 0 || chunk.EvalCount > 0)
                            yield return new StreamDelta.UsageDelta(chunk.PromptEvalCount, chunk.EvalCount, 0);
                        yield break;
                    }
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
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/tags");
                if (!string.IsNullOrEmpty(_apiKey))
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var response = await SharedHttp.SendAsync(httpRequest, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return (IReadOnlyList<string>)Array.Empty<string>();
                var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken: cancellationToken);
                return tags?.Models?.Select(m => m.Name).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
            }, maxRetries: 2, initialDelay: TimeSpan.FromSeconds(0.5),
            shouldRetry: ex => ex is HttpRequestException,
            cancellationToken: cancellationToken);
        }
        catch { return []; }
    }

    private OllamaChatRequest BuildRequest(ProviderRequest request, bool stream)
    {
        var messages = request.Messages.Select(m => new OllamaMessage(
            Role: m.Role.ToLowerInvariant(),
            Content: m.Content ?? "",
            ToolCalls: m.ToolCalls?.Select(tc => new OllamaToolCall(
                Id: tc.Id, Type: "function",
                Function: new OllamaToolCallFunction(
                    JsonSerializer.Deserialize<JsonElement>(tc.Arguments),
                    tc.Name))).ToList(),
            ToolCallId: m.ToolCallId)).ToList();

        var tools = request.Tools.Count > 0
            ? request.Tools.Select(t => new OllamaTool(
                Function: new OllamaFunction(t.Name, t.Description,
                    JsonSerializer.Deserialize<JsonElement>(t.ParametersJson)))).ToList()
            : null;

        return new OllamaChatRequest(
            Model: request.ModelId,
            Messages: messages,
            Stream: stream,
            Tools: tools,
            Options: new OllamaOptions(
                Temperature: request.Temperature,
                TopP: request.TopP,
                NumPredict: request.MaxTokens,
                NumCtx: 32768),
            Think: false);
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

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}

public record OllamaTagsResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("models")] List<OllamaModelInfo>? Models);
