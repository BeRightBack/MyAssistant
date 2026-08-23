using MyAssistant.Abstractions.Agent;
using MyAssistant.Abstractions.Services;
using MyAssistant.Core.Tools;
using Xunit;

namespace MyAssistant.Tests;

public class KnowledgeToolTests
{
    private static ToolCallContext Ctx(string args, string sessionId = "sess-1") =>
        new(sessionId, args, @"C:\temp");

    [Fact]
    public async Task StoreKnowledge_StoresAndReportsId()
    {
        var fake = new FakeSessionService();
        var tool = new StoreKnowledgeTool(() => fake);

        var result = await tool.ExecuteAsync(Ctx("""{"category":"preference","content":"Uses tabs"}"""));

        Assert.False(result.IsError);
        Assert.Contains("Stored knowledge #100", result.Content);
        Assert.Single(fake.Knowledge);
        Assert.Equal("preference", fake.Knowledge[0].Category);
    }

    [Fact]
    public async Task StoreKnowledge_SkipsExactDuplicate_AnyCategory()
    {
        var fake = new FakeSessionService();
        fake.Knowledge.Add(new KnowledgeEntry(7, "sess-1", "global", "My timezone is America/Toronto", null, DateTime.UtcNow, DateTime.UtcNow));
        var tool = new StoreKnowledgeTool(() => fake);

        var result = await tool.ExecuteAsync(Ctx("""{"category":"preference","content":"  my timezone is america/toronto "}"""));

        Assert.False(result.IsError);
        Assert.Contains("Already stored as #7 in category 'global'", result.Content);
        Assert.Single(fake.Knowledge); // nothing inserted
    }

    [Fact]
    public async Task StoreKnowledge_DuplicateInOtherSession_IsStored()
    {
        var fake = new FakeSessionService();
        fake.Knowledge.Add(new KnowledgeEntry(7, "other-session", "general", "Some fact", null, DateTime.UtcNow, DateTime.UtcNow));
        var tool = new StoreKnowledgeTool(() => fake);

        await tool.ExecuteAsync(Ctx("""{"category":"general","content":"Some fact"}"""));

        Assert.Equal(2, fake.Knowledge.Count); // session-scoped dedup only
    }

    [Theory]
    [InlineData("""{"category":"","content":"x"}""")]
    [InlineData("""{"category":"pref","content":""}""")]
    [InlineData("{}")]
    public async Task StoreKnowledge_MissingArgs_ReturnsError(string args)
    {
        var tool = new StoreKnowledgeTool(() => new FakeSessionService());
        var result = await tool.ExecuteAsync(Ctx(args));
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task SearchKnowledge_ReturnsFormattedHits()
    {
        var fake = new FakeSessionService();
        fake.Knowledge.Add(new KnowledgeEntry(9, "sess-1", "general", "The production database is atlas-prod hosted in eu-west-1", null, new DateTime(2026, 8, 23), DateTime.UtcNow));
        var tool = new SearchKnowledgeTool(() => fake);

        var result = await tool.ExecuteAsync(Ctx("""{"query":"production database"}"""));

        Assert.False(result.IsError);
        Assert.Contains("Found 1 knowledge entries", result.Content);
        Assert.Contains("[9] (general) 2026-08-23", result.Content);
        Assert.Contains("atlas-prod", result.Content);
    }

    [Fact]
    public async Task SearchKnowledge_NoHits_ReportsEmpty()
    {
        var tool = new SearchKnowledgeTool(() => new FakeSessionService());
        var result = await tool.ExecuteAsync(Ctx("""{"query":"nothing matches this"}"""));
        Assert.Contains("No matching knowledge found", result.Content);
    }

    [Fact]
    public async Task ManageList_ShowsEntriesFromAllSessions()
    {
        var fake = new FakeSessionService();
        fake.Knowledge.Add(new KnowledgeEntry(3, "aaaa1111-xxxx", "global", "Timezone fact", null, DateTime.UtcNow, DateTime.UtcNow));
        fake.Knowledge.Add(new KnowledgeEntry(4, "bbbb2222-yyyy", "preference", "Tabs fact", null, DateTime.UtcNow, DateTime.UtcNow));
        var tool = new ManageKnowledgeTool(() => fake);

        var result = await tool.ExecuteAsync(Ctx("""{"action":"list"}"""));

        Assert.False(result.IsError);
        Assert.Contains("across all sessions (2)", result.Content);
        Assert.Contains("session aaaa1111", result.Content);
        Assert.Contains("session bbbb2222", result.Content);
        Assert.Contains(fake.Calls, c => c == "listAll");
    }

    [Fact]
    public async Task ManageDelete_RemovesById()
    {
        var fake = new FakeSessionService();
        fake.Knowledge.Add(new KnowledgeEntry(5, "s", "general", "old", null, DateTime.UtcNow, DateTime.UtcNow));
        var tool = new ManageKnowledgeTool(() => fake);

        var result = await tool.ExecuteAsync(Ctx("""{"action":"delete","entry_id":5}"""));

        Assert.Contains("Deleted knowledge #5", result.Content);
        Assert.Empty(fake.Knowledge);
    }

    [Fact]
    public async Task ManageDelete_MissingId_ReturnsError()
    {
        var tool = new ManageKnowledgeTool(() => new FakeSessionService());
        var result = await tool.ExecuteAsync(Ctx("""{"action":"delete"}"""));
        Assert.True(result.IsError);
    }
}
