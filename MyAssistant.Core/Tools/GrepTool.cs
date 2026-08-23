using System.Text.Json;
using System.Text.RegularExpressions;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public class GrepTool : ITool
{
    public string Name => "grep";
    public string Description => "Search file contents using regex. Returns matching lines with file paths and line numbers.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""pattern"": { ""type"": ""string"", ""description"": ""Regex pattern to search for"" },
            ""path"": { ""type"": ""string"", ""description"": ""Absolute directory path to search in, e.g. C:\\Users\\Administrator\\Documents\\MyAssistant. Always use the full absolute path."" },
            ""include"": { ""type"": ""string"", ""description"": ""File pattern to include (e.g. *.cs) (optional)"" }
        },
        ""required"": [""pattern"", ""path""]
    }");

    public Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        var args = JsonSerializer.Deserialize<GrepArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        if (args?.Pattern == null)
            return Task.FromResult(new ToolResult("Error: pattern is required", true));

        var searchDir = string.IsNullOrEmpty(args.Path)
            ? context.WorkingDirectory
            : ToolSandbox.ValidateAndResolve(args.Path, context.WorkingDirectory);

        if (!Directory.Exists(searchDir))
            return Task.FromResult(new ToolResult($"Error: Directory not found: {searchDir}", true));

        try
        {
            var regex = new Regex(args.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var filePattern = string.IsNullOrEmpty(args.Include) ? "*.*" : args.Include;
            var files = Directory.GetFiles(searchDir, filePattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            });

            var matches = new List<string>();
            foreach (var file in files)
            {
                if (matches.Count >= 200) break;
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            var relPath = Path.GetRelativePath(searchDir, file);
                            matches.Add($"{relPath}:{i + 1}: {lines[i].Trim()}");
                            if (matches.Count >= 200) break;
                        }
                    }
                }
                catch { }
            }

            var result = matches.Count > 0
                ? string.Join("\n", matches)
                : "No matches found.";

            return Task.FromResult(new ToolResult(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Error: {ex.Message}", true));
        }
    }
}

public record GrepArgs(string? Pattern = null, string? Path = null, string? Include = null);
