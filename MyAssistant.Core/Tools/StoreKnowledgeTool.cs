using System.Text.Json;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;

namespace MyAssistant.Core.Tools;

public class StoreKnowledgeTool : ITool
{
    private readonly Func<ISessionService> _sessionServiceFactory;

    public string Name => "store_knowledge";
    public string Description => "Store important information for future reference. Use for: project decisions, code conventions, user preferences, architecture patterns, debugging insights, or anything worth remembering across sessions.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""category"": { ""type"": ""string"", ""description"": ""Category for the knowledge (e.g., 'decision', 'convention', 'preference', 'pattern', 'insight', 'general')"" },
            ""content"": { ""type"": ""string"", ""description"": ""The knowledge to store. Be specific and self-contained so it makes sense out of context, and include the key terms a future search might use (synonyms welcome). Example: 'Production PostgreSQL database / prod DB is named atlas-prod, hosted in eu-west-1 region.'"" },
            ""metadata"": { ""type"": ""string"", ""description"": ""Optional JSON metadata (e.g., related files, tags)"" }
        },
        ""required"": [""category"", ""content""]
    }");

    public StoreKnowledgeTool(Func<ISessionService> sessionServiceFactory)
    {
        _sessionServiceFactory = sessionServiceFactory;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        var args = JsonSerializer.Deserialize<StoreKnowledgeArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        if (string.IsNullOrEmpty(args?.Category) || string.IsNullOrEmpty(args?.Content))
            return new ToolResult("Error: category and content are required", true);

        try
        {
            var sessionService = _sessionServiceFactory();

            // Skip if identical content already stored in this session (any category)
            var existing = await sessionService.ListKnowledgeAsync(context.SessionId, cancellationToken: cancellationToken);
            var duplicate = existing.FirstOrDefault(e =>
                e.Content.Trim().Equals(args.Content!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (duplicate != null)
                return new ToolResult($"Already stored as #{duplicate.Id} in category '{duplicate.Category}' — not duplicating.");

            var id = await sessionService.StoreKnowledgeAsync(
                context.SessionId, args.Category, args.Content, args.Metadata, cancellationToken);
            return new ToolResult($"Stored knowledge #{id} in category '{args.Category}'");
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error storing knowledge: {ex.Message}", true);
        }
    }
}

public record StoreKnowledgeArgs(string? Category = null, string? Content = null, string? Metadata = null);
