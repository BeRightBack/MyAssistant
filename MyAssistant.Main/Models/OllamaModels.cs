using System.Text.Json.Serialization;

namespace MyAssistant.Main.Models;

public record OllamaModelsResponse(
    [property: JsonPropertyName("models")] List<OllamaModelInfo>? Models);

public record OllamaModelInfo(
    [property: JsonPropertyName("name")] string Name);
