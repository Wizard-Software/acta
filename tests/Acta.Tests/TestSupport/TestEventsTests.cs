using Xunit;

namespace Acta.Tests.TestSupport;

/// <summary>Self-tests for the <see cref="TestEvents"/> kit — determinism and distinctness guarantees.</summary>
public sealed class TestEventsTests
{
    [Fact]
    public void DeterministicId_SameSeed_ReturnsSameGuid()
    {
        TestEvents.DeterministicId("cmd-1").Should().Be(TestEvents.DeterministicId("cmd-1"));
    }

    [Fact]
    public void DeterministicId_DifferentSeeds_ReturnDifferentGuids()
    {
        TestEvents.DeterministicId("cmd-2").Should().NotBe(TestEvents.DeterministicId("cmd-1"));
    }

    [Fact]
    public void OrderPlaced_NoArgument_ReturnsDistinctEventIds()
    {
        TestEvents.OrderPlaced().EventId.Should().NotBe(TestEvents.OrderPlaced().EventId);
    }

    [Fact]
    public void OrderPlaced_WithExplicitId_UsesIt()
    {
        var id = TestEvents.DeterministicId("cmd-42");

        TestEvents.OrderPlaced(id).EventId.Should().Be(id);
    }

    [Fact]
    public void Distinct_ReturnsRequestedCountWithUniqueEventIds()
    {
        var batch = TestEvents.Distinct(5);

        batch.Length.Should().Be(5);
        batch.Select(e => e.EventId).Distinct().Count().Should().Be(5);
    }
}
