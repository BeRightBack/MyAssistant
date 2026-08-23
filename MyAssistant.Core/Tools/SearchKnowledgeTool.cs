using System.Text;
using System.Text.Json;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;

namespace MyAssistant.Core.Tools;

public class SearchKnowledgeTool : ITool
{
    private readonly Func<ISessionService> _sessionServiceFactory;

    public string Name => "search_knowledge";
    public string Description => "Search stored knowledge for relevant information. Use before answering questions about project conventions, past decisions, or context you may not have in your current conversation.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""query"": { ""type"": ""string"", ""description"": ""Search query to find relevant knowledge"" },
            ""category"": { ""type"": ""string"", ""description"": ""Optional: filter by category"" },
            ""limit"": { ""type"": ""integer"", ""description"": ""Max results to return (default 10)"" }
        },
        ""required"": [""query""]
    }");

    public SearchKnowledgeTool(Func<ISessionService> sessionServiceFactory)
    {
        _sessionServiceFactory = sessionServiceFactory;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        var args = JsonSerializer.Deserialize<SearchKnowledgeArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        if (string.IsNullOrEmpty(args?.Query))
            return new ToolResult("Error: query is required", true);

        try
        {
            var sessionService = _sessionServiceFactory();
            var results = await sessionService.SearchKnowledgeAsync(
                context.SessionId, args.Query, args.Limit ?? 10, args.Category, cancellationToken);

            if (results.Length == 0)
                return new ToolResult("No matching knowledge found.");

            var sb = new StringBuilder();
            sb.AppendLine($"Found {results.Length} knowledge entries:");
            foreach (var entry in results)
            {
                sb.AppendLine($"\n[{entry.Id}] ({entry.Category}) {entry.CreatedAt:yyyy-MM-dd}");
                sb.AppendLine(entry.Content);
                if (!string.IsNullOrEmpty(entry.MetadataJson))
                    sb.AppendLine($"Metadata: {entry.MetadataJson}");
            }
            return new ToolResult(sb.ToString());
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error searching knowledge: {ex.Message}", true);
        }
    }
}

public record SearchKnowledgeArgs(string? Query = null, string? Category = null, int? Limit = null);
