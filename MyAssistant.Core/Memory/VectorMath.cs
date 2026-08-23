using System.Text.Json;

namespace MyAssistant.Core.Memory;

/// <summary>
/// Vector helpers for semantic knowledge retrieval: cosine similarity and
/// float[] <-> JSON serialization for storing embeddings in the database.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Cosine similarity in [-1, 1]. Returns 0 for mismatched dimensions,
    /// empty inputs, or zero-magnitude vectors.
    /// </summary>
    public static float CosineSimilarity(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length)
            return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0f || normB == 0f)
            return 0f;

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    public static string ToJson(IReadOnlyList<float> vector) =>
        JsonSerializer.Serialize(vector);

    public static float[]? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<float[]>(json);
        }
        catch
        {
            return null;
        }
    }
}
