using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public sealed class WebSearchTool : ITool
{
    public string Name => "web_search";
    public string Description => "Search the web for current information. Use for facts, recipes, news, documentation, or anything not in your training data.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""query"": { ""type"": ""string"", ""description"": ""Search query"" }
        },
        ""required"": [""query""]
    }");

    private static readonly HttpClient _http = CreateHttp();
    private static readonly ConcurrentDictionary<string, (string Result, DateTime Expiry)> _cache = new();
    private static readonly ConcurrentDictionary<string, DateTime> _rateLimited = new();

    public async Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<SearchArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        var query = args?.Query?.Trim().Trim('"') ?? "";
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult("Error: empty search query", true);

        var results = await ExecuteSearchAsync(query, ct);
        return new ToolResult(results);
    }

    public static async Task<string> ExecuteSearchAsync(string query, CancellationToken ct = default)
    {
        var cacheKey = query.ToLowerInvariant().Trim();

        // Return cached result (success or rate-limit) to avoid hammering DDG
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
            return cached.Result;

        // If recently rate-limited, don't retry yet
        if (_rateLimited.TryGetValue(cacheKey, out var retryAfter) && DateTime.UtcNow < retryAfter)
            return $"Search rate-limited. Try again in {((retryAfter - DateTime.UtcNow).TotalSeconds):0}s.";

        var results = await SearchDuckDuckGoHtmlAsync(query, ct);

        if (results.Count == 0)
            results = await SearchBraveHtmlAsync(query, ct);

        if (results.Count == 0)
        {
            // Try to auto-fetch URLs found in the query (e.g. "jalf.com features")
            var autoFetched = await AutoFetchDomainsAsync(query, ct);
            if (autoFetched != null)
                return autoFetched;

            _rateLimited[cacheKey] = DateTime.UtcNow.AddSeconds(30);
            return "Web search returned no results. Use web_fetch to access specific URLs directly.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Web search results for: {query}");
        sb.AppendLine();
        for (int i = 0; i < Math.Min(results.Count, 8); i++)
        {
            var r = results[i];
            sb.AppendLine($"{i + 1}. {r.Title}");
            sb.AppendLine($"   {r.Url}");
            if (!string.IsNullOrEmpty(r.Snippet))
                sb.AppendLine($"   {r.Snippet}");
            sb.AppendLine();
        }
        var result = sb.ToString().TrimEnd();
        _cache[cacheKey] = (result, DateTime.UtcNow + TimeSpan.FromMinutes(5));
        return result;
    }

    private static async Task<List<SearchResult>> SearchDuckDuckGoHtmlAsync(string query, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["q"] = query,
                    ["kl"] = "wt-wt"
                })
            };
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "text/html");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == (HttpStatusCode)429)
                return new List<SearchResult>();

            if (!response.IsSuccessStatusCode)
                return new List<SearchResult>();

            var html = await response.Content.ReadAsStringAsync(ct);
            return ParseDuckDuckGoHtml(html);
        }
        catch { return new List<SearchResult>(); }
    }

    private static List<SearchResult> ParseDuckDuckGoHtml(string html)
    {
        var results = new List<SearchResult>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var resultDivs = doc.DocumentNode.SelectNodes("//div[contains(@class,'web-result')]");
        if (resultDivs == null) return results;

        foreach (var div in resultDivs)
        {
            var titleLink = div.SelectSingleNode(".//a[contains(@class,'result__a')]");
            if (titleLink == null) continue;

            var title = WebUtility.HtmlDecode(titleLink.InnerText.Trim());
            if (string.IsNullOrWhiteSpace(title)) continue;

            var href = titleLink.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href)) continue;

            if (href.Contains("uddg="))
            {
                var m = Regex.Match(href, @"uddg=([^&]+)");
                if (m.Success) href = Uri.UnescapeDataString(m.Groups[1].Value);
            }
            else if (href.StartsWith("//"))
            {
                href = "https:" + href;
            }

            var snippetNode = div.SelectSingleNode(".//a[contains(@class,'result__snippet')]");
            var snippet = snippetNode != null
                ? WebUtility.HtmlDecode(Regex.Replace(snippetNode.InnerText, @"\s+", " ").Trim())
                : "";

            results.Add(new SearchResult { Title = title, Url = href, Snippet = snippet });
        }
        return results;
    }

    private static async Task<List<SearchResult>> SearchBraveHtmlAsync(string query, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://search.brave.com/search?q={Uri.EscapeDataString(query)}&source=web");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new List<SearchResult>();

            var html = await response.Content.ReadAsStringAsync(ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var results = new List<SearchResult>();
            var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'snippet')]");
            if (nodes == null) return results;

            foreach (var node in nodes)
            {
                var a = node.SelectSingleNode(".//a[@href]");
                if (a == null) continue;
                var title = WebUtility.HtmlDecode(a.InnerText.Trim());
                var href = a.GetAttributeValue("href", "");
                var snip = node.SelectSingleNode(".//p[contains(@class,'snippet-description')]");
                var snippet = snip != null ? WebUtility.HtmlDecode(snip.InnerText.Trim()) : "";
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(href) && href.StartsWith("http"))
                    results.Add(new SearchResult { Title = title, Url = href, Snippet = snippet });
            }
            return results;
        }
        catch { return new List<SearchResult>(); }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static async Task<string?> AutoFetchDomainsAsync(string query, CancellationToken ct)
    {
        try
        {
            // Extract domain-like patterns from the query
            var domainPattern = @"\b([a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}\b";
            var matches = Regex.Matches(query, domainPattern);
            var fetched = new StringBuilder();
            int count = 0;

            foreach (Match m in matches)
            {
                if (count >= 3) break;
                var domain = m.Value;
                if (domain.StartsWith("www.")) domain = domain[4..];
                if (string.IsNullOrEmpty(domain)) continue;

                var urls = new[] { $"https://{domain}", $"https://www.{domain}" };
                foreach (var url in urls)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                        request.Headers.Add("Accept", "text/html");

                        using var response = await _http.SendAsync(request, ct);
                        if (!response.IsSuccessStatusCode) continue;

                        var html = await response.Content.ReadAsStringAsync(ct);
                        var text = ExtractTextFromHtml(html);
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        fetched.AppendLine($"[CONTENT FROM {url}]");
                        fetched.AppendLine(text);
                        fetched.AppendLine($"[END {url}]");
                        fetched.AppendLine();
                        count++;
                        break;
                    }
                    catch { }
                }
            }

            if (fetched.Length > 0)
                return $"Could not search the web, but fetched content from the relevant site(s) directly:\n\n{fetched}";
        }
        catch { }
        return null;
    }

    private static string ExtractTextFromHtml(string html)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        // Remove noise
        foreach (var tag in new[] { "script", "style", "noscript", "nav", "footer", "header", "aside" })
        {
            var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
            if (nodes != null) foreach (var n in nodes) n.Remove();
        }

        var main = doc.DocumentNode.SelectSingleNode("//main") ??
                    doc.DocumentNode.SelectSingleNode("//article") ??
                    doc.DocumentNode.SelectSingleNode("//body");
        if (main == null) return "";

        var text = WebUtility.HtmlDecode(main.InnerText);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 8000 ? text[..8000] + "..." : text;
    }

    private class SearchResult
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Snippet { get; set; } = "";
    }

    private record SearchArgs(string? Query = null);
}
