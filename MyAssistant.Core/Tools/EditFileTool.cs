using System.Text.Json;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public class EditFileTool : ITool
{
    public string Name => "edit_file";
    public string Description => "Edit a file by finding and replacing exact text. The old_string must match exactly.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""path"": { ""type"": ""string"", ""description"": ""Absolute or relative path to the file"" },
            ""old_string"": { ""type"": ""string"", ""description"": ""Exact text to find and replace"" },
            ""new_string"": { ""type"": ""string"", ""description"": ""Text to replace with"" }
        },
        ""required"": [""path"", ""old_string"", ""new_string""]
    }");

    public Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        EditFileArgs? args;
        try
        {
            args = JsonSerializer.Deserialize<EditFileArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ToolResult(
                $"Error: Invalid JSON arguments ({ex.Message}). Inside strings, escape newlines as \\n and double quotes as \\\".", true));
        }

        if (args?.Path == null || args.OldString == null || args.NewString == null)
            return Task.FromResult(new ToolResult("Error: path, old_string, and new_string are required", true));

        var fullPath = ToolSandbox.ValidateAndResolve(args.Path, context.WorkingDirectory);
        if (!File.Exists(fullPath))
            return Task.FromResult(new ToolResult($"Error: File not found: {fullPath}", true));

        var content = File.ReadAllText(fullPath);
        var count = content.Split(args.OldString).Length - 1;

        if (count == 0)
            return Task.FromResult(new ToolResult($"Error: old_string not found in {fullPath}", true));

        if (count > 1)
            return Task.FromResult(new ToolResult($"Error: old_string found {count} times in {fullPath}. Provide more context to make it unique.", true));

        var newContent = content.Replace(args.OldString, args.NewString);
        File.WriteAllText(fullPath, newContent);

        return Task.FromResult(new ToolResult($"File edited: {fullPath}"));
    }
}

public record EditFileArgs(string? Path = null, string? OldString = null, string? NewString = null);
