using Xunit;

namespace Acta.Tests.TestSupport;

/// <summary>Self-tests for the <see cref="TestEvents"/> kit — determinism and distinctness guarantees.</summary>
public sealed class TestEventsTests
{
    [Fact]
    public void DeterministicId_SameSeed_ReturnsSameGuid()
    {
        Assert.Equal(TestEvents.DeterministicId("cmd-1"), TestEvents.DeterministicId("cmd-1"));
    }

    [Fact]
    public void DeterministicId_DifferentSeeds_ReturnDifferentGuids()
    {
        Assert.NotEqual(TestEvents.DeterministicId("cmd-1"), TestEvents.DeterministicId("cmd-2"));
    }

    [Fact]
    public void OrderPlaced_NoArgument_ReturnsDistinctEventIds()
    {
        Assert.NotEqual(TestEvents.OrderPlaced().EventId, TestEvents.OrderPlaced().EventId);
    }

    [Fact]
    public void OrderPlaced_WithExplicitId_UsesIt()
    {
        var id = TestEvents.DeterministicId("cmd-42");

        Assert.Equal(id, TestEvents.OrderPlaced(id).EventId);
    }

    [Fact]
    public void Distinct_ReturnsRequestedCountWithUniqueEventIds()
    {
        var batch = TestEvents.Distinct(5);

        Assert.Equal(5, batch.Length);
        Assert.Equal(5, batch.Select(e => e.EventId).Distinct().Count());
    }
}
