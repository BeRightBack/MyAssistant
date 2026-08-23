using MyAssistant.Core.Memory;
using Xunit;

namespace MyAssistant.Tests;

public class VectorMathTests
{
    [Fact]
    public void IdenticalVectors_HaveSimilarityOne()
    {
        var v = new float[] { 0.1f, -0.4f, 0.9f };
        Assert.Equal(1f, VectorMath.CosineSimilarity(v, v), 5);
    }

    [Fact]
    public void OrthogonalVectors_HaveSimilarityZero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity(new float[] { 1, 0 }, new float[] { 0, 1 }), 5);
    }

    [Fact]
    public void OppositeVectors_HaveSimilarityMinusOne()
    {
        Assert.Equal(-1f, VectorMath.CosineSimilarity(new float[] { 1, 2 }, new float[] { -1, -2 }), 5);
    }

    [Theory]
    [InlineData(new float[] { 1, 2 }, new float[] { 1 })]
    [InlineData(new float[] { }, new float[] { 1 })]
    public void MismatchedOrEmptyVectors_ReturnZero(float[] a, float[] b)
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity(a, b), 5);
    }

    [Fact]
    public void NullOrZeroMagnitude_ReturnZero()
    {
        Assert.Equal(0f, VectorMath.CosineSimilarity(null, new float[] { 1 }));
        Assert.Equal(0f, VectorMath.CosineSimilarity(new float[] { 0, 0 }, new float[] { 1, 1 }), 5);
    }

    [Fact]
    public void JsonRoundTrip_PreservesValues()
    {
        var v = new float[] { 0.25f, -1.5f, 3.75f };
        var json = VectorMath.ToJson(v);
        var back = VectorMath.FromJson(json);

        Assert.NotNull(back);
        Assert.Equal(v, back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void InvalidJson_ReturnsNull(string? json)
    {
        Assert.Null(VectorMath.FromJson(json));
    }
}
