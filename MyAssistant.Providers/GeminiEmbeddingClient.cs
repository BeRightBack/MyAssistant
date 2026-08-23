using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MyAssistant.Providers;

/// <summary>
/// Text-embedding client for Google Gemini (generativelanguage.googleapis.com).
/// Used to enable semantic (vector) recall for long-term knowledge.
/// Returns null on any failure so callers can degrade gracefully to keyword search.
/// </summary>
public class GeminiEmbeddingClient
{
    public const string DefaultModel = "gemini-embedding-001";

    private static readonly HttpClient Http = CreateHttp();
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiEmbeddingClient(string apiKey, string? model = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!;
    }

    public string Model => _model;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Keep request size sane; memory entries are short anyway
        var trimmed = text.Length > 4000 ? text[..4000] : text;

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:embedContent?key={_apiKey}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new EmbedRequest(new EmbedContent(new[] { new EmbedPart(trimmed) }))),
                    Encoding.UTF8,
                    "application/json")
            };

            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (doc.RootElement.TryGetProperty("embedding", out var embedding) &&
                embedding.TryGetProperty("values", out var values) &&
                values.ValueKind == JsonValueKind.Array)
            {
                var vector = new List<float>(values.GetArrayLength());
                foreach (var v in values.EnumerateArray())
                    vector.Add(v.GetSingle());
                return vector.ToArray();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyAssistant/1.0");
        return client;
    }

    private sealed record EmbedRequest(EmbedContent content);
    private sealed record EmbedContent(EmbedPart[] parts);
    private sealed record EmbedPart(string text);
}
