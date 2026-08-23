using MyAssistant.Abstractions.Agent;
using MyAssistant.Core.Agent;
using Xunit;

namespace MyAssistant.Tests;

public class ContextTrimmerTests
{
    private static ChatMessage UserMsg(string text, int repeats = 1)
        => new("user", string.Concat(Enumerable.Repeat(text, repeats)));

    [Fact]
    public void SmallConversation_IsUntouched()
    {
        var msgs = new List<ChatMessage>
        {
            new("system", "sys"),
            new("user", "hello"),
            new("assistant", "hi there")
        };
        ChatAgent.FitToContextBudget(msgs, 131072, 16384);

        Assert.Equal(3, msgs.Count);
        Assert.Equal("sys", msgs[0].Content);
        Assert.Equal("hello", msgs[1].Content);
        Assert.Equal("hi there", msgs[2].Content);
    }

    [Fact]
    public void OversizedToolResult_IsTruncated()
    {
        var big = new string('x', 100_000);
        var msgs = new List<ChatMessage>
        {
            new("system", "sys"),
            new("user", "read the file"),
            new("assistant", Content: null, ToolCalls: new List<ChatToolCall> { new("t1", "read_file", "{\"path\":\"a.cs\"}") }),
            new("tool", big, ToolCallId: "t1")
        };
        ChatAgent.FitToContextBudget(msgs, 131072, 16384);

        var tool = Assert.Single(msgs, m => m.Role == "tool");
        Assert.True(tool.Content!.Length < 20000);
        Assert.Contains("truncated", tool.Content);
    }

    [Fact]
    public void OldestGroups_Dropped_KeepsSystemAndNewest()
    {
        var filler = new string('a', 40_000); // ~10k tokens each
        var msgs = new List<ChatMessage> { new("system", "sys") };
        for (int i = 0; i < 15; i++)
        {
            msgs.Add(new("user", $"{filler} u{i}"));
            msgs.Add(new("assistant", $"{filler} a{i}"));
        }
        // ~310k tokens total vs budget 131072-16384-2500 = 112188
        ChatAgent.FitToContextBudget(msgs, 131072, 16384);

        Assert.Equal("sys", msgs[0].Content);
        Assert.Contains("omitted", msgs[1].Content);
        // newest pair must survive intact
        Assert.EndsWith("u14", msgs[^2].Content);
        Assert.EndsWith("a14", msgs[^1].Content);
        // and the total must now fit
        long total = msgs.Sum(m => (long)((m.Content?.Length ?? 0) + 3) / 4 + 8);
        Assert.True(total <= 112188);
    }

    [Fact]
    public void AssistantToolCall_NeverSeparatedFromItsResults()
    {
        var big = new string('b', 30_000);
        var msgs = new List<ChatMessage>
        {
            new("system", "sys"),
            new("user", "go"),
            new("assistant", Content: null, ToolCalls: new List<ChatToolCall> { new("t1", "grep", "{}") }),
            new("tool", big, ToolCallId: "t1"),
            new("assistant", "done explaining"),
            new("user", big),
            new("assistant", big)
        };

        ChatAgent.FitToContextBudget(msgs, 131072, 16384);

        for (int i = 0; i < msgs.Count; i++)
        {
            if (msgs[i].Role == "tool" && msgs[i].ToolCallId == "t1")
                Assert.True(i > 0 && msgs[i - 1].Role == "assistant" && msgs[i - 1].ToolCalls is { Count: > 0 },
                    "tool result was separated from its assistant tool-call message");
        }
    }

    [Fact]
    public void OverBudget_DropsOlderGroups_KeepsNewestIntact()
    {
        var giant = new string('g', 600_000); // ~150k tokens
        var msgs = new List<ChatMessage>
        {
            new("system", "sys"),
            new("user", giant),
            new("assistant", "answer")
        };
        ChatAgent.FitToContextBudget(msgs, 131072, 16384);

        // The older group must go so the request fits; newest stays.
        Assert.EndsWith("answer", msgs[^1].Content);
        Assert.DoesNotContain(msgs, m => m.Content == giant);
        Assert.Contains(msgs, m => m.Role == "system" && m.Content!.Contains("omitted"));
    }

    [Fact]
    public void SingleGroup_NeverDropped_EvenIfOverBudget()
    {
        var giant = new string('g', 600_000);
        var msgs = new List<ChatMessage>
        {
            new("system", "sys"),
            new("user", giant)
        };
        ChatAgent.FitToContextBudget(msgs, 131072, 16384);

        // With nothing newer to protect, we cannot drop the only group.
        Assert.Equal(2, msgs.Count);
        Assert.DoesNotContain(msgs, m => m.Content != null && m.Content.Contains("omitted"));
    }

    [Fact]
    public void EstimateTokens_BehavesSanely()
    {
        Assert.Equal(0, ChatAgent.EstimateTokens(null));
        Assert.Equal(0, ChatAgent.EstimateTokens(""));
        Assert.Equal(1, ChatAgent.EstimateTokens("abcd"));
        Assert.Equal(1, ChatAgent.EstimateTokens("ab"));
        Assert.Equal(1000, ChatAgent.EstimateTokens(new string('x', 4000)));
    }
}
