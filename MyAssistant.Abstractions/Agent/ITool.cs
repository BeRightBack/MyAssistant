using System.Collections.Immutable;
using System.Text.Json;

namespace MyAssistant.Abstractions.Agent;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default);
}

public record ToolCallContext(
    string SessionId,
    string ArgumentsJson,
    string WorkingDirectory);

public record ToolResult(
    string Content,
    bool IsError = false);
