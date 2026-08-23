using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MyAssistant.Abstractions.Agent;
using MyAssistant.Core.Agent;
using MyAssistant.Providers;
using Xunit;

namespace MyAssistant.Tests;

internal sealed class ScriptedProvider : IStreamingProvider
{
    private readonly List<StreamDelta[]> _script;
    public int Calls;
    public ProviderRequest? LastRequest;

    public ScriptedProvider(params StreamDelta[][] script) => _script = script.ToList();

    public string ProviderId => "scripted";

    public async IAsyncEnumerable<StreamDelta> StreamAsync(
        ProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;
        foreach (var delta in _script[Math.Min(Calls - 1, _script.Count - 1)])
        {
            await Task.Yield();
            yield return delta;
        }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

public class AgentEmptyResponseTests
{
    private static AgentCall Call() => new(
        "s1", "which database do we deploy to?",
        Provider: null!, // forces ChatAgent to use the injected ScriptedProvider
        ModelId: "m",
        Attachments: ImmutableArray<Attachment>.Empty,
        MaxOutputTokens: 16384, Temperature: 0.7, TopP: 1.0, History: null);

    [Fact]
    public async Task EmptyResponseAfterToolRound_IsNudgedIntoFinalAnswer()
    {
        var provider = new ScriptedProvider(
            new[] { (StreamDelta)new StreamDelta.ToolCallStart("t1", "search_knowledge"), new StreamDelta.Done() },
            new[] { (StreamDelta)new StreamDelta.Done() },
            new[] { (StreamDelta)new StreamDelta.TextContent("The database is atlas-prod."), new StreamDelta.Done() });
        var agent = new ChatAgent(provider, "m", Array.Empty<ITool>(), "sys", 10, 131072, null);

        var events = new List<AgentEvent>();
        await foreach (var e in agent.RunAsync(Call()))
            events.Add(e);

        Assert.Equal(3, provider.Calls);
        Assert.Contains(events, e => e is AgentEvent.TextDelta t && t.Delta.Contains("atlas-prod"));
        var done = Assert.Single(events.OfType<AgentEvent.Done>());
        Assert.Null(done.Error);
    }

    [Fact]
    public async Task PersistentEmptyResponses_TerminateWithoutInfiniteLoop()
    {
        var provider = new ScriptedProvider(
            new[] { (StreamDelta)new StreamDelta.ToolCallStart("t1", "search_knowledge") },
            new[] { (StreamDelta)new StreamDelta.Done() },
            new[] { (StreamDelta)new StreamDelta.Done() });
        var agent = new ChatAgent(provider, "m", Array.Empty<ITool>(), "sys", 10, 131072, null);

        var events = new List<AgentEvent>();
        await foreach (var e in agent.RunAsync(Call()))
            events.Add(e);

        Assert.Equal(3, provider.Calls);
        Assert.Single(events.OfType<AgentEvent.Done>());
    }

    [Fact]
    public async Task OrphanedToolCalls_AreStripped_FromReplayedHistory()
    {
        // Simulates the poisoned state stored after dead turns: an assistant
        // message with a tool call whose result was never persisted.
        var history = new List<ChatMessage>
        {
            new("user", "which database do we deploy to?"),
            new("assistant", Content: null,
                ToolCalls: new List<ChatToolCall> { new("t1", "search_knowledge", "{}") })
        };
        var provider = new ScriptedProvider(
            new[] { (StreamDelta)new StreamDelta.TextContent("atlas-prod"), new StreamDelta.Done() });
        var agent = new ChatAgent(provider, "m", Array.Empty<ITool>(), "sys", 10, 131072, null);
        var call = new AgentCall("s1", "try again",
            Provider: null!, ModelId: "m",
            Attachments: ImmutableArray<Attachment>.Empty,
            MaxOutputTokens: 16384, Temperature: 0.7, TopP: 1.0, History: history);

        await foreach (var _ in agent.RunAsync(call)) { }

        Assert.NotNull(provider.LastRequest);
        // NOTE: Messages is the agent's live list; by assert time it also
        // contains the final scripted assistant reply ("atlas-prod").
        Assert.DoesNotContain(provider.LastRequest!.Messages, m => m.ToolCalls != null);
        Assert.DoesNotContain(provider.LastRequest.Messages, m => m.Role == "assistant" && string.IsNullOrEmpty(m.Content));
        // system + original user + current prompt (+ final scripted answer)
        Assert.Equal(4, provider.LastRequest.Messages.Count);
        Assert.Equal(new[] { "system", "user", "user", "assistant" },
            provider.LastRequest.Messages.Select(m => m.Role).ToArray());
    }

    [Fact]
    public async Task CompleteToolPairs_InHistory_ArePreserved()
    {
        var history = new List<ChatMessage>
        {
            new("user", "q1"),
            new("assistant", Content: null,
                ToolCalls: new List<ChatToolCall> { new("t1", "grep", "{}") }),
            new("tool", "no matches", ToolCallId: "t1"),
            new("assistant", "Nothing found.")
        };
        var provider = new ScriptedProvider(
            new[] { (StreamDelta)new StreamDelta.TextContent("ok"), new StreamDelta.Done() });
        var agent = new ChatAgent(provider, "m", Array.Empty<ITool>(), "sys", 10, 131072, null);
        var call = new AgentCall("s1", "q2",
            Provider: null!, ModelId: "m",
            Attachments: ImmutableArray<Attachment>.Empty,
            MaxOutputTokens: 16384, Temperature: 0.7, TopP: 1.0, History: history);

        await foreach (var _ in agent.RunAsync(call)) { }

        var msgs = provider.LastRequest!.Messages;
        Assert.Equal(7, msgs.Count); // system + 4 history + prompt (+ final scripted answer)
        Assert.Contains(msgs, m => m.ToolCalls is { Count: > 0 });
        Assert.Contains(msgs, m => m.Role == "tool" && m.ToolCallId == "t1");
    }
}
