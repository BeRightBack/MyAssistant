using System.Text.Json;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public class ListDirectoryTool : ITool
{
    public string Name => "list_directory";
    public string Description => "List files and directories in a path. Shows file sizes and whether entries are directories.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""path"": { ""type"": ""string"", ""description"": ""Absolute path to the directory to list, e.g. C:\\Users\\Administrator\\Documents\\MyAssistant. Always use the full absolute path."" }
        },
        ""required"": [""path""]
    }");

    public Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        ListDirArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<ListDirArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ToolResult(
                $"Error: Invalid JSON arguments ({ex.Message}). Correct format: {{\"path\": \"C:\\\\Users\\\\Administrator\\\\Documents\\\\MyAssistant\"}}", true));
        }

        var dir = string.IsNullOrEmpty(args?.Path)
            ? context.WorkingDirectory
            : ToolSandbox.ValidateAndResolve(args.Path, context.WorkingDirectory);

        if (!Directory.Exists(dir))
        {
            var hint = ToolSandbox.FindClosestExistingHint(dir);
            return Task.FromResult(new ToolResult(
                $"Error: Directory not found: {dir}.{hint}", true));
        }

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Directory: {dir}");
            sb.AppendLine();

            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(d);
                sb.AppendLine($"  [DIR]  {name}/");
            }

            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                var size = new FileInfo(f).Length;
                sb.AppendLine($"  {FormatSize(size),8}  {name}");
            }

            return Task.FromResult(new ToolResult(sb.ToString()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Error: {ex.Message}", true));
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1}MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1}GB"
    };
}

public record ListDirArgs(string? Path = null);
