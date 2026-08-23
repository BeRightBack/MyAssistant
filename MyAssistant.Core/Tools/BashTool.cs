using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public class BashTool : ITool
{
    public string Name => "bash";
    public string Description => "Execute a shell command. Use for running build commands, git, npm, etc. Returns stdout and stderr.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""command"": { ""type"": ""string"", ""description"": ""Shell command to execute"" },
            ""timeout"": { ""type"": ""integer"", ""description"": ""Timeout in seconds (optional, default 30)"" }
        },
        ""required"": [""command""]
    }");

    public async Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        var args = JsonSerializer.Deserialize<BashArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        if (args?.Command == null)
            return new ToolResult("Error: command is required", true);

        var cmd = args.Command.Trim();
        string shell = "cmd.exe";
        string shellArgs;

        if (cmd.StartsWith("ls ") || cmd == "ls")
        {
            shell = "powershell.exe";
            var path = cmd.Length > 2 ? cmd[3..].Trim() : "";
            shellArgs = $"-NoProfile -Command \"Get-ChildItem {(string.IsNullOrEmpty(path) ? "" : "'" + path.Trim('\'', '"') + "'")} | Select-Object -ExpandProperty Name\"";
        }
        else if (cmd.StartsWith("cat "))
        {
            shell = "powershell.exe";
            var path = cmd[4..].Trim().Trim('\'', '"');
            shellArgs = $"-NoProfile -Command \"Get-Content '{path}'\"";
        }
        else if (cmd == "pwd")
        {
            shell = "powershell.exe";
            shellArgs = "-NoProfile -Command \"Get-Location\"";
        }
        else
        {
            shellArgs = $"/c {cmd}";
        }

        if (!ToolSandbox.IsCommandAllowed(cmd))
            return new ToolResult("Error: This command is blocked by the sandbox for safety.", true);

        var timeout = TimeSpan.FromSeconds(args.Timeout ?? 30);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = shellArgs,
                WorkingDirectory = context.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new ToolResult("Error: Failed to start process", true);

            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            await process.WaitForExitAsync(cts.Token);

            var result = new StringBuilder();
            if (output.Length > 0)
                result.AppendLine(output.ToString().TrimEnd());
            if (error.Length > 0)
                result.AppendLine($"STDERR:\n{error.ToString().TrimEnd()}");

            return new ToolResult(result.Length > 0 ? result.ToString() : "(no output)");
        }
        catch (OperationCanceledException)
        {
            return new ToolResult($"Error: Command timed out after {timeout.TotalSeconds}s", true);
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error: {ex.Message}", true);
        }
    }
}

public record BashArgs(string? Command = null, int? Timeout = null);
