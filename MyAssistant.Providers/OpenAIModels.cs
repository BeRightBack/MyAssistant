using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAssistant.Providers;

public record OpenAIChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<OpenAIMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("tools")] List<OpenAITool>? Tools = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("top_p")] double? TopP = null,
    [property: JsonPropertyName("max_tokens")] long? MaxTokens = null,
    [property: JsonPropertyName("stream_options")] OpenAIStreamOptions? StreamOptions = null);

public record OpenAIStreamOptions(
    [property: JsonPropertyName("include_usage")] bool IncludeUsage = true);

public record OpenAIMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("tool_calls")] List<OpenAIToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

public record OpenAITool(
    [property: JsonPropertyName("function")] OpenAIFunction Function,
    [property: JsonPropertyName("type")] string Type = "function");

public record OpenAIFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

public record OpenAIToolCall(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("index")] int? Index,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("function")] OpenAIToolCallFunction? Function,
    [property: JsonPropertyName("extra_content")] JsonElement? ExtraContent = null);

public record OpenAIToolCallFunction(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("arguments")] string? Arguments);

public record OpenAIChatResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("choices")] List<OpenAIChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAIUsage? Usage);

public record OpenAIChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] OpenAIMessage? Delta,
    [property: JsonPropertyName("message")] OpenAIMessage? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

public record OpenAIUsage(
    [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
    [property: JsonPropertyName("completion_tokens")] long CompletionTokens,
    [property: JsonPropertyName("total_tokens")] long TotalTokens);

public record OpenAIStreamChunk(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("choices")] List<OpenAIChoice>? Choices,
    [property: JsonPropertyName("usage")] OpenAIUsage? Usage);
