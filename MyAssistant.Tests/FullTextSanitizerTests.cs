using MyAssistant.Core.Memory;
using Xunit;

namespace MyAssistant.Tests;

public class FullTextSanitizerTests
{
    [Fact]
    public void OperatorsAreStripped()
    {
        Assert.Equal("C   rocks", FullTextSanitizer.Sanitize("C++ rocks"));
        Assert.Equal("flag  value", FullTextSanitizer.Sanitize("-flag +value"));
        Assert.Equal("alpha   beta", FullTextSanitizer.Sanitize("(alpha) ~beta"));
        Assert.Equal("quoted  phrase", FullTextSanitizer.Sanitize("\"quoted\" phrase"));
    }

    [Fact]
    public void PlainTextIsUnchanged()
    {
        Assert.Equal("which region hosts our DB cluster", FullTextSanitizer.Sanitize("which region hosts our DB cluster"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+-~")]
    [InlineData("*()")]
    public void EmptyOrOperatorOnlyQueries_ReturnNull(string? query)
    {
        Assert.Null(FullTextSanitizer.Sanitize(query));
    }

    [Fact]
    public void OperatorOnlyAfterStrip_ReturnsNull()
    {
        Assert.Null(FullTextSanitizer.Sanitize("-- ++"));
    }
}
