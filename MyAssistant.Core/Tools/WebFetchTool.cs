using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public class WebFetchTool : ITool
{
    private static readonly HttpClient _http = CreateHttp();

    public string Name => "web_fetch";
    public string Description => "Fetch content from a URL. Returns readable text content of the page, stripping navigation, scripts, and ads.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""url"": { ""type"": ""string"", ""description"": ""URL to fetch"" },
            ""max_length"": { ""type"": ""integer"", ""description"": ""Max characters to return (optional, default 15000)"" }
        },
        ""required"": [""url""]
    }");

    public async Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        var args = JsonSerializer.Deserialize<WebFetchArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        if (args?.Url == null)
            return new ToolResult("Error: url is required", true);

        var url = args.Url.Trim();
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var response = await _http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            string result;
            if (contentType.Contains("html"))
            {
                result = ExtractTextFromHtml(content, url);
            }
            else if (contentType.Contains("json"))
            {
                result = FormatJsonContent(content);
            }
            else
            {
                result = content;
            }

            var maxLen = args.MaxLength ?? 15000;
            if (result.Length > maxLen)
                result = result[..maxLen] + "\n\n... (truncated)";

            return new ToolResult(result);
        }
        catch (HttpRequestException ex)
        {
            return new ToolResult($"Error fetching URL: {ex.Message}", true);
        }
        catch (TaskCanceledException)
        {
            return new ToolResult("Error: request timed out", true);
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error: {ex.Message}", true);
        }
    }

    private static string ExtractTextFromHtml(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove noise elements
        var removeSelectors = new[]
        {
            "//script", "//style", "//noscript", "//iframe",
            "//svg", "//form", "//input", "//button",
            "//nav", "//footer", "//header",
            "//div[contains(@class,'ad')]", "//div[contains(@class,'cookie')]",
            "//div[contains(@class,'popup')]", "//div[contains(@class,'modal')]",
            "//div[contains(@class,'sidebar')]", "//div[contains(@class,'menu')]",
            "//div[contains(@class,'nav')]", "//div[contains(@class,'header')]",
            "//div[contains(@class,'footer')]", "//div[contains(@class,'banner')]",
            "//aside", "//center"
        };

        foreach (var selector in removeSelectors)
        {
            try
            {
                var nodes = doc.DocumentNode.SelectNodes(selector);
                if (nodes != null)
                {
                    foreach (var node in nodes)
                        node.Remove();
                }
            }
            catch { }
        }

        // Try to find main content area
        var mainContent = doc.DocumentNode.SelectSingleNode("//main") ??
                          doc.DocumentNode.SelectSingleNode("//article") ??
                          doc.DocumentNode.SelectSingleNode("//div[contains(@class,'content')]") ??
                          doc.DocumentNode.SelectSingleNode("//div[contains(@class,'main')]") ??
                          doc.DocumentNode.SelectSingleNode("//div[@id='content']") ??
                          doc.DocumentNode.SelectSingleNode("//div[@id='main']") ??
                          doc.DocumentNode.SelectSingleNode("//body");

        if (mainContent == null)
            return "";

        // Extract title
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        var title = titleNode?.InnerText.Trim() ?? "";

        // Extract meta description
        var metaDesc = doc.DocumentNode.SelectSingleNode("//meta[@name='description']");
        var description = metaDesc?.GetAttributeValue("content", "") ?? "";

        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.AppendLine($"# {HtmlEntity.DeEntitize(title)}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.AppendLine(HtmlEntity.DeEntitize(description));
            sb.AppendLine();
        }

        // Extract text with structure
        ExtractNodeText(mainContent, sb, 0);

        var result = sb.ToString();

        // Clean up excessive whitespace
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        result = result.Trim();

        // Add source URL
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            result += $"\n\n---\nSource: {baseUrl}";
        }

        return result;
    }

    private static void ExtractNodeText(HtmlNode node, StringBuilder sb, int depth)
    {
        if (depth > 10) return;

        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                var text = HtmlEntity.DeEntitize(child.InnerText).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    sb.Append(text + " ");
            }
            else if (child.NodeType == HtmlNodeType.Element)
            {
                var tag = child.Name.ToLowerInvariant();

                // Skip hidden elements
                var style = child.GetAttributeValue("style", "");
                if (style.Contains("display:none") || style.Contains("display: none") || style.Contains("visibility:hidden"))
                    continue;

                switch (tag)
                {
                    case "h1":
                    case "h2":
                    case "h3":
                    case "h4":
                    case "h5":
                    case "h6":
                        sb.AppendLine();
                        sb.AppendLine($"## {HtmlEntity.DeEntitize(child.InnerText.Trim())}");
                        sb.AppendLine();
                        break;

                    case "p":
                    case "div":
                    case "section":
                    case "article":
                        sb.AppendLine();
                        ExtractNodeText(child, sb, depth + 1);
                        sb.AppendLine();
                        break;

                    case "li":
                        sb.Append("- ");
                        ExtractNodeText(child, sb, depth + 1);
                        sb.AppendLine();
                        break;

                    case "br":
                        sb.AppendLine();
                        break;

                    case "a":
                        var href = child.GetAttributeValue("href", "");
                        var linkText = HtmlEntity.DeEntitize(child.InnerText.Trim());
                        if (!string.IsNullOrWhiteSpace(linkText) && !string.IsNullOrWhiteSpace(href))
                        {
                            if (!href.StartsWith("http") && !href.StartsWith("//"))
                            {
                                try
                                {
                                    var baseUri = new Uri("https://placeholder.com");
                                    href = new Uri(baseUri, href).ToString();
                                }
                                catch { }
                            }
                            sb.Append($"[{linkText}]({href})");
                        }
                        else if (!string.IsNullOrWhiteSpace(linkText))
                        {
                            sb.Append(linkText);
                        }
                        break;

                    case "table":
                        sb.AppendLine();
                        ExtractTableText(child, sb);
                        sb.AppendLine();
                        break;

                    case "pre":
                    case "code":
                        sb.AppendLine();
                        sb.AppendLine("```");
                        sb.AppendLine(HtmlEntity.DeEntitize(child.InnerText.Trim()));
                        sb.AppendLine("```");
                        sb.AppendLine();
                        break;

                    case "blockquote":
                        sb.AppendLine();
                        var quoteText = HtmlEntity.DeEntitize(child.InnerText.Trim());
                        foreach (var line in quoteText.Split('\n'))
                            sb.AppendLine($"> {line.Trim()}");
                        sb.AppendLine();
                        break;

                    default:
                        ExtractNodeText(child, sb, depth + 1);
                        break;
                }
            }
        }
    }

    private static void ExtractTableText(HtmlNode table, StringBuilder sb)
    {
        var rows = table.SelectNodes(".//tr");
        if (rows == null) return;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("th|td");
            if (cells == null) continue;

            var cellTexts = cells.Select(c => HtmlEntity.DeEntitize(c.InnerText.Trim())).ToList();
            sb.AppendLine("| " + string.Join(" | ", cellTexts) + " |");
        }
    }

    private static string FormatJsonContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        return client;
    }
}

public record WebFetchArgs(string? Url = null, int? MaxLength = null);
