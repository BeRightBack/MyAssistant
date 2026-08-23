using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Providers;

public interface IStreamingProvider
{
    string ProviderId { get; }
    IAsyncEnumerable<StreamDelta> StreamAsync(
        ProviderRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
}

public record ProviderRequest(
    string ModelId,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    double Temperature,
    double TopP,
    long MaxTokens);

public abstract record StreamDelta
{
    public sealed record TextContent(string Text) : StreamDelta;
    public sealed record ThinkingContent(string Text) : StreamDelta;
    public sealed record ToolCallStart(string Id, string Name, string? ThoughtSignature = null) : StreamDelta;
    public sealed record ToolCallArgumentDelta(string ArgumentsDelta) : StreamDelta;
    public sealed record UsageDelta(long PromptTokens, long CompletionTokens, long TotalTokens) : StreamDelta;
    public sealed record Error(string Message) : StreamDelta;
    public sealed record Done() : StreamDelta;
}
