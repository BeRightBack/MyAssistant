namespace MyAssistant.Core.Memory;

/// <summary>
/// Detects "remember that ..."/"note globally: ..." style prefixes that make the
/// app store knowledge automatically, without waiting for an LLM round-trip.
/// </summary>
public static class MemoryTriggerParser
{
    private static readonly string[] GlobalPrefixes =
    {
        "remember globally that ",
        "remember globally: ",
        "store globally that ",
        "note globally that ",
        "keep globally in mind that ",
        "note globally: "
    };

    private static readonly string[] SessionPrefixes =
    {
        "remember that ",
        "remember: ",
        "store that ",
        "note that ",
        "keep in mind that ",
        "note: "
    };

    /// <summary>
    /// Attempts to parse a memory-store trigger phrase.
    /// Returns true with <paramref name="category"/> ("global"/"general") and the
    /// remaining content when the message starts with a trigger prefix.
    /// </summary>
    public static bool TryParse(string? userInput, out string category, out string content)
    {
        category = "";
        content = "";

        if (string.IsNullOrWhiteSpace(userInput))
            return false;

        foreach (var prefix in GlobalPrefixes)
        {
            if (userInput.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Finish(userInput[prefix.Length..], "global", out category, out content);
        }

        foreach (var prefix in SessionPrefixes)
        {
            if (userInput.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Finish(userInput[prefix.Length..], "general", out category, out content);
        }

        return false;
    }

    private static bool Finish(string rawContent, string category, out string outCategory, out string outContent)
    {
        var trimmed = rawContent.Trim();
        if (trimmed.Length == 0)
        {
            outCategory = "";
            outContent = "";
            return false;
        }

        outCategory = category;
        outContent = trimmed;
        return true;
    }
}
