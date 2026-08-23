using System.Text.Json;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "Write content to a file. Creates the file if it doesn't exist, overwrites if it does.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""path"": { ""type"": ""string"", ""description"": ""Absolute or relative path to the file"" },
            ""content"": { ""type"": ""string"", ""description"": ""The content to write"" }
        },
        ""required"": [""path"", ""content""]
    }");

    public Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        WriteFileArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<WriteFileArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ToolResult(
                $"Error: Invalid JSON arguments ({ex.Message}). Inside \"content\", escape newlines as \\n and double quotes as \\\".", true));
        }

        if (args?.Path == null || args.Content == null)
            return Task.FromResult(new ToolResult("Error: path and content are required", true));

        try
        {
            var fullPath = ToolSandbox.ValidateAndResolve(args.Path, context.WorkingDirectory);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            bool existed = File.Exists(fullPath);
            File.WriteAllText(fullPath, args.Content);
            return Task.FromResult(new ToolResult(
                $"{(existed ? "Overwrote existing file" : "Created new file")}: {fullPath} ({args.Content.Length} chars)"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Error writing file: {ex.Message}", true));
        }
    }
}

public record WriteFileArgs(string? Path = null, string? Content = null);
