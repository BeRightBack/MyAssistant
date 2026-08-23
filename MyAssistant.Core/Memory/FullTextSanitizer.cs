using System.Text.RegularExpressions;

namespace MyAssistant.Core.Memory;

/// <summary>
/// Prepares raw user text for MySQL FULLTEXT boolean-mode matching by stripping
/// operator characters (+ - > < ( ) ~ * ") so input like "C++" or "-flag"
/// cannot silently alter query semantics.
/// </summary>
public static partial class FullTextSanitizer
{
    [GeneratedRegex(@"[+\-><()~*@""]")]
    private static partial Regex OperatorChars();

    /// <summary>
    /// Returns the sanitized query, or null when nothing search-worthy remains.
    /// </summary>
    public static string? Sanitize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var cleaned = OperatorChars().Replace(query, " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
