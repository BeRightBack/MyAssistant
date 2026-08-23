using System.Text.Json;

namespace MyAssistant.Core.Tools;

public static class ToolJsonOptions
{
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public static class PathHelper
{
    public static string Resolve(string? path, string workingDirectory)
    {
        var p = path ?? "";
        if (p.StartsWith("~/") || p.StartsWith("~\\"))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]);
        else if (p.StartsWith("~"))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[1..]);
        return Path.GetFullPath(p, workingDirectory);
    }

    public static string ResolveProjectPath(string? path, string workingDirectory)
    {
        var resolved = Resolve(path, workingDirectory);
        if (!Directory.Exists(resolved)) return resolved;

        var found = FindProjectDir(resolved, 0);
        return found ?? resolved;
    }

    private static string? FindProjectDir(string dir, int depth)
    {
        if (depth > 3) return null;

        var hasCsproj = Directory.GetFiles(dir, "*.csproj").Length > 0;
        var hasSln = Directory.GetFiles(dir, "*.sln").Length > 0 || Directory.GetFiles(dir, "*.slnx").Length > 0;
        if (hasCsproj || hasSln) return dir;

        foreach (var sub in Directory.GetDirectories(dir))
        {
            var result = FindProjectDir(sub, depth + 1);
            if (result != null) return result;
        }

        return null;
    }
}
