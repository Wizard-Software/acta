using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class GlobalPositionTests
{
    [Fact]
    public void Start_IsZero()
    {
        GlobalPosition.Start.Value.Should().Be(0);
    }

    [Fact]
    public void CompareTo_OrdersByValue()
    {
        var lower = new GlobalPosition(1);
        var higher = new GlobalPosition(2);

        (lower.CompareTo(higher) < 0).Should().BeTrue();
        (higher.CompareTo(lower) > 0).Should().BeTrue();
        lower.CompareTo(new GlobalPosition(1)).Should().Be(0);
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

        (a < b).Should().Be(expectedLess);
        (a > b).Should().Be(expectedGreater);
        (a <= b).Should().Be(expectedLessOrEqual);
        (a >= b).Should().Be(expectedGreaterOrEqual);
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var a = new GlobalPosition(42);
        var b = new GlobalPosition(42);

        b.Should().Be(a);
        (a == b).Should().BeTrue();
    }
}
