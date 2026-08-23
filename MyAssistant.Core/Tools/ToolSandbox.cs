namespace MyAssistant.Core.Tools;

public static class ToolSandbox
{
    private static readonly HashSet<string> BlockedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), ""),
        @"C:\Windows\System32\",
        @"C:\Windows\SysWOW64\",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ""),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), ""),
        @"C:\ProgramData\",
    };

    private static string? _workingDirectory;

    public static string WorkingDirectory
    {
        get => _workingDirectory ?? Environment.CurrentDirectory;
        set => _workingDirectory = value;
    }

    public static bool IsPathAllowed(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);

        foreach (var blocked in BlockedPaths)
        {
            if (!string.IsNullOrEmpty(blocked) && normalized.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public static string ValidateAndResolve(string? path, string? contextWorkingDir, bool useProjectResolver = false)
    {
        var resolved = useProjectResolver
            ? PathHelper.ResolveProjectPath(path, contextWorkingDir ?? WorkingDirectory)
            : PathHelper.Resolve(path, contextWorkingDir ?? WorkingDirectory);

        if (!IsPathAllowed(resolved))
            throw new InvalidOperationException($"Access denied: {resolved} is outside the allowed working directory.");

        return resolved;
    }

    public static bool IsCommandAllowed(string command)
    {
        var trimmed = command.Trim().ToLowerInvariant();

        var blocked = new[]
        {
            "format ", "del /s", "rd /s", "rmdir /s",
            "takeown", "icacls", "cacls",
            "reg delete", "regedit",
            "bcdedit", "diskpart",
            "shutdown", "logoff",
            "taskkill /f", "taskkill /pid",
        };

        foreach (var b in blocked)
        {
            if (trimmed.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    public static string FindClosestExistingHint(string missingPath)
    {
        try
        {
            var current = Path.GetFullPath(missingPath);
            for (int i = 0; i < 6 && current != null; i++)
            {
                var parent = Path.GetDirectoryName(current);
                if (parent == null || !Directory.Exists(parent))
                {
                    current = parent;
                    continue;
                }

                var entries = Directory.GetFileSystemEntries(parent)
                    .Select(Path.GetFileName)
                    .Take(15);
                return $" Closest existing parent: {parent}. Its contents: {string.Join(", ", entries)}";
            }
        }
        catch { }
        return "";
    }
}
