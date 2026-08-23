namespace MyAssistant.Abstractions.Configuration;

public record ModelInfo(
    string Id,
    string Name,
    string ProviderId,
    long ContextWindow,
    long DefaultMaxTokens);
