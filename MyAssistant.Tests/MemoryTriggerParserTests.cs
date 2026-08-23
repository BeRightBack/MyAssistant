using MyAssistant.Core.Memory;
using Xunit;

namespace MyAssistant.Tests;

public class MemoryTriggerParserTests
{
    [Theory]
    [InlineData("remember that we deploy on fridays", "general", "we deploy on fridays")]
    [InlineData("REMEMBER THAT we deploy on fridays", "general", "we deploy on fridays")]
    [InlineData("remember: deploy key is abc123", "general", "deploy key is abc123")]
    [InlineData("note that the build is flaky", "general", "the build is flaky")]
    [InlineData("keep in mind that staging uses http", "general", "staging uses http")]
    public void SessionTriggers_AreParsed(string input, string expectedCategory, string expectedContent)
    {
        var ok = MemoryTriggerParser.TryParse(input, out var category, out var content);
        Assert.True(ok);
        Assert.Equal(expectedCategory, category);
        Assert.Equal(expectedContent, content);
    }

    [Theory]
    [InlineData("remember globally that my timezone is America/Toronto", "global", "my timezone is America/Toronto")]
    [InlineData("remember globally: prod is atlas-prod", "global", "prod is atlas-prod")]
    [InlineData("note globally: I prefer dark mode", "global", "I prefer dark mode")]
    public void GlobalTriggers_AreParsed(string input, string expectedCategory, string expectedContent)
    {
        var ok = MemoryTriggerParser.TryParse(input, out var category, out var content);
        Assert.True(ok);
        Assert.Equal(expectedCategory, category);
        Assert.Equal(expectedContent, content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Please remember that we use tabs")]   // prefix must start the message
    [InlineData("I want you to remember that x")]      // not a prefix match
    [InlineData("remember that      ")]                // no content after prefix
    [InlineData("What's our production database?")]    // ordinary question
    public void NonTriggers_ReturnFalse(string? input)
    {
        var ok = MemoryTriggerParser.TryParse(input, out _, out _);
        Assert.False(ok);
    }
}
