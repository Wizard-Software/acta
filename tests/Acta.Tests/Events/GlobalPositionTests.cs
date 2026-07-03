using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class GlobalPositionTests
{
    [Fact]
    public void Start_IsZero()
    {
        Assert.Equal(0, GlobalPosition.Start.Value);
    }

    [Fact]
    public void CompareTo_OrdersByValue()
    {
        var lower = new GlobalPosition(1);
        var higher = new GlobalPosition(2);

        Assert.True(lower.CompareTo(higher) < 0);
        Assert.True(higher.CompareTo(lower) > 0);
        Assert.Equal(0, lower.CompareTo(new GlobalPosition(1)));
    }

    [Theory]
    [InlineData(1, 2, true, false, true, false)]
    [InlineData(2, 1, false, true, false, true)]
    [InlineData(1, 1, false, false, true, true)]
    public void Operators_LessGreaterLessEqualGreaterEqual_MatchValue(
        long aValue, long bValue, bool expectedLess, bool expectedGreater, bool expectedLessOrEqual, bool expectedGreaterOrEqual)
    {
        var a = new GlobalPosition(aValue);
        var b = new GlobalPosition(bValue);

        Assert.Equal(expectedLess, a < b);
        Assert.Equal(expectedGreater, a > b);
        Assert.Equal(expectedLessOrEqual, a <= b);
        Assert.Equal(expectedGreaterOrEqual, a >= b);
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var a = new GlobalPosition(42);
        var b = new GlobalPosition(42);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }
}
