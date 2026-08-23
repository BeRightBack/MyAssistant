using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyAssistant.Providers;

public record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<OllamaMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("tools")] List<OllamaTool>? Tools = null,
    [property: JsonPropertyName("options")] OllamaOptions? Options = null,
    [property: JsonPropertyName("think")] bool? Think = null);

public record OllamaMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content = "",
    [property: JsonPropertyName("thinking")] string? Thinking = null,
    [property: JsonPropertyName("tool_calls")] List<OllamaToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

public record OllamaTool(
    [property: JsonPropertyName("function")] OllamaFunction Function,
    [property: JsonPropertyName("type")] string Type = "function");

public record OllamaFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

public record OllamaToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OllamaToolCallFunction Function);

public record OllamaToolCallFunction(
    [property: JsonPropertyName("arguments")] JsonElement Arguments,
    [property: JsonPropertyName("name")] string Name);

public record OllamaOptions(
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("top_p")] double? TopP = null,
    [property: JsonPropertyName("num_predict")] long? NumPredict = null,
    [property: JsonPropertyName("num_ctx")] long? NumCtx = null);

public record OllamaChatResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("message")] OllamaMessage Message,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("prompt_eval_count")] long PromptEvalCount,
    [property: JsonPropertyName("eval_count")] long EvalCount,
    [property: JsonPropertyName("prompt_eval_duration")] long PromptEvalDuration,
    [property: JsonPropertyName("eval_duration")] long EvalDuration);

public record OllamaModelInfo(
    [property: JsonPropertyName("name")] string Name);
