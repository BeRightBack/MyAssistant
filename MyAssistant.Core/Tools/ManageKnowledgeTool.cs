using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;

namespace MyAssistant.Core.Tools;

public class ManageKnowledgeTool : ITool
{
    private readonly Func<ISessionService> _sessionServiceFactory;

    public string Name => "manage_knowledge";
    public string Description => "List or delete stored knowledge. Use to review what knowledge exists, clean up outdated entries, or remove incorrect information.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""action"": { ""type"": ""string"", ""enum"": [""list"", ""delete"", ""delete_category""], ""description"": ""Action: 'list' to view ALL entries across sessions, 'delete' to remove by ID, 'delete_category' to remove all in a category (current session only)"" },
            ""entry_id"": { ""type"": ""integer"", ""description"": ""ID of the entry to delete (required for 'delete' action)"" },
            ""category"": { ""type"": ""string"", ""description"": ""Category to list or delete (required for 'delete_category', optional filter for 'list')"" }
        },
        ""required"": [""action""]
    }");

    public ManageKnowledgeTool(Func<ISessionService> sessionServiceFactory)
    {
        _sessionServiceFactory = sessionServiceFactory;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        var args = JsonSerializer.Deserialize<ManageKnowledgeArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        if (string.IsNullOrEmpty(args?.Action))
            return new ToolResult("Error: action is required", true);

        try
        {
            var sessionService = _sessionServiceFactory();

            switch (args.Action.ToLowerInvariant())
            {
                case "list":
                {
                    var entries = await sessionService.ListAllKnowledgeAsync(args.Category, 200, cancellationToken);
                    if (entries.Length == 0)
                        return new ToolResult("No knowledge entries found.");
                    var sb = new StringBuilder();
                    sb.AppendLine($"Knowledge entries across all sessions ({entries.Length}):");
                    foreach (var e in entries)
                    {
                        var preview = e.Content.Length > 100 ? e.Content[..100] + "..." : e.Content;
                        sb.AppendLine($"  [{e.Id}] ({e.Category}, session {e.SessionId[..8]}) {preview}");
                    }
                    return new ToolResult(sb.ToString());
                }
                case "delete":
                {
                    if (args.EntryId == null)
                        return new ToolResult("Error: entry_id is required for delete", true);
                    var deleted = await sessionService.DeleteKnowledgeAsync(args.EntryId.Value, cancellationToken);
                    return new ToolResult(deleted ? $"Deleted knowledge #{args.EntryId}" : $"Knowledge #{args.EntryId} not found");
                }
                case "delete_category":
                {
                    if (string.IsNullOrEmpty(args.Category))
                        return new ToolResult("Error: category is required for delete_category", true);
                    var count = await sessionService.DeleteKnowledgeByCategoryAsync(context.SessionId, args.Category, cancellationToken);
                    return new ToolResult($"Deleted {count} entries from category '{args.Category}'");
                }
                default:
                    return new ToolResult($"Error: unknown action '{args.Action}'. Use: list, delete, delete_category", true);
            }
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error managing knowledge: {ex.Message}", true);
        }
    }
}

public record ManageKnowledgeArgs(
    string? Action = null,
    [property: JsonPropertyName("entry_id")] long? EntryId = null,
    string? Category = null);
