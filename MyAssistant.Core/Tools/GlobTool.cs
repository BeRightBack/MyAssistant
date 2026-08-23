using System.Text.Json;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public class GlobTool : ITool
{
    public string Name => "glob";
    public string Description => "Find files matching a glob pattern (e.g. **/*.cs, src/**/*.ts). Returns matching file paths.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""pattern"": { ""type"": ""string"", ""description"": ""Glob pattern to match files (e.g. **/*.cs, src/**/*.ts)"" },
            ""path"": { ""type"": ""string"", ""description"": ""Absolute directory path to search in, e.g. C:\\Users\\Administrator\\Documents\\MyAssistant. Always use the full absolute path."" }
        },
        ""required"": [""pattern"", ""path""]
    }");

    public Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        GlobArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<GlobArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ToolResult(
                $"Error: Invalid JSON arguments ({ex.Message}). Correct format: {{\"pattern\": \"**/*.cs\", \"path\": \"C:\\\\Users\\\\Administrator\\\\Documents\\\\MyAssistant\"}}", true));
        }

        var searchDir = string.IsNullOrEmpty(args?.Path)
            ? context.WorkingDirectory
            : ToolSandbox.ValidateAndResolve(args.Path, context.WorkingDirectory);

        if (!Directory.Exists(searchDir))
            return Task.FromResult(new ToolResult(
                $"Error: Directory not found: {searchDir}.{ToolSandbox.FindClosestExistingHint(searchDir)}", true));

        // Be forgiving: default pattern matches everything
        var rawPattern = string.IsNullOrWhiteSpace(args!.Pattern) ? "**/*" : args.Pattern.Trim();

        try
        {
            List<string> files;

            if (rawPattern.Contains("**"))
            {
                // Recursive glob: extract the filename part after **/
                var extension = rawPattern.Replace("**/", "").Replace("**\\", "");
                if (string.IsNullOrEmpty(extension)) extension = "*";
                files = Directory.GetFiles(searchDir, extension, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                }).ToList();
            }
            else
            {
                files = Directory.GetFiles(searchDir, rawPattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                }).ToList();
            }

            var result = string.Join("\n", files.Take(200));
            if (files.Count > 200)
                result += $"\n... ({files.Count - 200} more files)";

            return Task.FromResult(new ToolResult(result.Length > 0 ? result : "(no files matched)"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Error: {ex.Message}. Try a simpler pattern like *.cs or *", true));
        }
    }
}

public record GlobArgs(string? Pattern = null, string? Path = null);
