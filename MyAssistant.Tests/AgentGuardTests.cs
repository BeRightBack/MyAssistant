using MyAssistant.Core.Agent;
using Xunit;

namespace MyAssistant.Tests;

public class AgentGuardTests
{
    [Theory]
    [InlineData("Here is the project structure overview:")]           // 2 indicators
    [InlineData("Below is a summary of the architecture document.")]  // 2 indicators
    public void LooksLikePrematureStop_FiresOnCodebaseSummaries(string response)
    {
        Assert.True(ChatAgent.LooksLikePrematureStop(response));
    }

    [Theory]
    [InlineData("Your timezone preference has been stored globally. Let me know what you'd like next.")]
    [InlineData("The production database is named atlas-prod.")]
    [InlineData("")]
    public void LooksLikePrematureStop_IgnoresNormalReplies(string response)
    {
        Assert.False(ChatAgent.LooksLikePrematureStop(response));
    }

    [Fact]
    public void LooksLikePrematureStop_RespectsReadFiles()
    {
        var response = "Here is a summary of the project structure after reading the source.";
        Assert.False(ChatAgent.LooksLikePrematureStop(response)); // mentions reading files
    }

    [Theory]
    [InlineData("I'll start by listing the directory. The file contains many classes.")] // 2 indicators
    [InlineData("Looking at the codebase — probably contains auth logic.")]              // 2 indicators
    public void LooksLikeHallucination_FiresWithoutReads(string response)
    {
        Assert.True(ChatAgent.LooksLikeHallucination(response));
    }

    [Theory]
    [InlineData("The answer is 42.")]
    [InlineData("Stored your preference successfully.")]
    public void LooksLikeHallucination_IgnoresNormalReplies(string response)
    {
        Assert.False(ChatAgent.LooksLikeHallucination(response));
    }
}
