using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MyAssistant.Abstractions.Configuration;

namespace MyAssistant.Abstractions.Agent;

public interface IAgent
{
    IAsyncEnumerable<AgentEvent> RunAsync(AgentCall call, CancellationToken cancellationToken = default);
    bool IsBusy();
    void Cancel(string sessionId);
    void CancelAll();
    void UpdateSystemPrompt(string prompt);
    string SystemPrompt { get; }
}

public record AgentCall(
    string SessionId,
    string Prompt,
    ProviderConfig Provider,
    string ModelId,
    ImmutableArray<Attachment> Attachments,
    long MaxOutputTokens,
    double Temperature,
    double TopP,
    IReadOnlyList<ChatMessage>? History = null);

public record ProviderConfig(
    string Id,
    string Name,
    string BaseUrl,
    string? ApiKey,
    ProviderKind Kind);

public enum ProviderKind
{
    OpenAICompatible,
    Ollama,
    Gemini,
    Groq,
    Mistral
}

public abstract record AgentEvent
{
    public sealed record TextDelta(string Delta) : AgentEvent;
    public sealed record ThinkingDelta(string Delta) : AgentEvent;
    public sealed record ToolCallStart(string Id, string Name, string ArgumentsJson) : AgentEvent;
    public sealed record ToolCallResult(string Id, string Name, string Content, bool IsError) : AgentEvent;
    public sealed record Usage(UsageTokens Tokens) : AgentEvent;
    public sealed record Done(string? Error = null) : AgentEvent;
}

public record UsageTokens(long PromptTokens, long CompletionTokens, long TotalTokens);

public record ChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    IReadOnlyList<ChatToolResult>? ToolResults = null,
    string? ToolCallId = null);

public record ChatToolCall(
    string Id,
    string Name,
    string Arguments,
    string? ThoughtSignature = null);

public record ChatToolResult(
    string ToolCallId,
    string Name,
    string Content,
    bool IsError);

public record ToolDefinition(
    string Name,
    string Description,
    string ParametersJson);

public record Attachment(
    string Id,
    string Name,
    string MimeType,
    byte[] Data);

public record Usage(
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens);

public record RunComplete(
    string SessionId,
    string? Error = null);
