using System.Collections.Immutable;
using System.Text.Json;
using MyAssistant.Abstractions.Agent;

namespace MyAssistant.Core.Tools;

public class TaskAgentTool : ITool
{
    private readonly Func<string, IReadOnlyList<ITool>, IAgent> _agentFactory;
    private readonly Func<IReadOnlyList<ITool>> _getParentTools;
    private readonly Func<ProviderConfig> _providerConfigFactory;
    private readonly Func<string> _modelIdFactory;

    public string Name => "task_agent";
    public string Description => "Spawn a sub-agent to handle a focused task. The sub-agent has its own context but shares tools with the parent. Use for: quick research, code snippets, file reads, or analysis tasks. Keep tasks focused and specific.";

    public ToolDefinition Definition => new(Name, Description, @"{
        ""type"": ""object"",
        ""properties"": {
            ""task"": { ""type"": ""string"", ""description"": ""Specific task for the sub-agent to accomplish"" }
        },
        ""required"": [""task""]
    }");

    public TaskAgentTool(Func<string, IReadOnlyList<ITool>, IAgent> agentFactory, Func<IReadOnlyList<ITool>> getParentTools, Func<ProviderConfig> providerConfigFactory, Func<string> modelIdFactory)
    {
        _agentFactory = agentFactory;
        _getParentTools = getParentTools;
        _providerConfigFactory = providerConfigFactory;
        _modelIdFactory = modelIdFactory;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext context, CancellationToken cancellationToken = default)
    {
        var args = JsonSerializer.Deserialize<TaskAgentArgs>(context.ArgumentsJson, ToolJsonOptions.CaseInsensitive);
        if (string.IsNullOrEmpty(args?.Task))
            return new ToolResult("Error: task description is required", true);

        var systemPrompt = "You are a focused assistant. Complete the given task concisely. " +
            "Use tools when needed. Return your final answer clearly.";

        try
        {
            var agent = _agentFactory(systemPrompt, _getParentTools());
            var subSessionId = $"sub_{Guid.NewGuid():N}";
            var providerConfig = _providerConfigFactory();
            var modelId = _modelIdFactory();
            var call = new AgentCall(
                SessionId: subSessionId,
                Prompt: args.Task,
                Provider: providerConfig,
                ModelId: modelId,
                Attachments: ImmutableArray<Attachment>.Empty,
                MaxOutputTokens: 4096,
                Temperature: 0.7,
                TopP: 0.95);

            var resultBuilder = new System.Text.StringBuilder();
            await foreach (var evt in agent.RunAsync(call, cancellationToken))
            {
                if (evt is AgentEvent.TextDelta td)
                    resultBuilder.Append(td.Delta);
                else if (evt is AgentEvent.Done done && done.Error != null)
                    return new ToolResult($"Sub-agent error: {done.Error}", true);
            }

            var result = resultBuilder.ToString();
            if (string.IsNullOrEmpty(result))
                return new ToolResult("Sub-agent completed with no output");

            return new ToolResult(result.Length > 8000 ? result[..8000] + "\n... (truncated)" : result);
        }
        catch (OperationCanceledException)
        {
            return new ToolResult("Sub-agent timed out", true);
        }
        catch (Exception ex)
        {
            return new ToolResult($"Sub-agent failed: {ex.Message}", true);
        }
    }
}

public record TaskAgentArgs(string? Task = null);
