using System.Text.Json;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "Read the contents of a file. Returns the full content with line numbers.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""path"": { ""type"": ""string"", ""description"": ""Absolute path to the file, e.g. C:\\Users\\Administrator\\Documents\\MyAssistant\\MyAssistant.Core\\Agent\\ChatAgent.cs. Always use the full absolute path."" },
            ""offset"": { ""type"": ""integer"", ""description"": ""Line number to start from (1-indexed, optional)"" },
            ""limit"": { ""type"": ""integer"", ""description"": ""Max number of lines to read (optional, default 2000)"" }
        },
        ""required"": [""path""]
    }");

    public Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        var args = JsonSerializer.Deserialize<ReadFileArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        if (args?.Path == null)
            return Task.FromResult(new ToolResult("Error: path is required", true));

        var fullPath = ToolSandbox.ValidateAndResolve(args.Path, context.WorkingDirectory);
        if (!File.Exists(fullPath))
            return Task.FromResult(new ToolResult($"Error: File not found: {fullPath}", true));

        var lines = File.ReadAllLines(fullPath);
        var offset = Math.Max(0, (args.Offset ?? 1) - 1);
        var limit = args.Limit ?? 2000;
        var selected = lines.Skip(offset).Take(limit);

        var sb = new System.Text.StringBuilder();
        int lineNum = offset + 1;
        foreach (var line in selected)
        {
            sb.AppendLine($"{lineNum}: {line}");
            lineNum++;
        }

        if (lines.Length > offset + limit)
            sb.AppendLine($"\n... ({lines.Length - offset - limit} more lines)");

        return Task.FromResult(new ToolResult(sb.ToString()));
    }
}

public record ReadFileArgs(string? Path = null, int? Offset = null, int? Limit = null);
