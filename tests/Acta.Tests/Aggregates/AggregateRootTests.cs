using Xunit;

using Acta.Tests.TestSupport;

namespace Acta.Tests.Aggregates;

/// <summary>
/// Unit tests for <c>AggregateRoot</c> mechanics (task 3.1): <c>Raise</c> applies state before
/// queueing and advances <c>Version</c>; <c>LoadFromHistory</c> folds history without populating
/// <c>UncommittedEvents</c>; <c>ClearUncommittedEvents</c> empties the queue while keeping
/// <c>Version</c>; null guards on both entry points; an unknown event type is a no-op.
/// </summary>
public sealed class AggregateRootTests
{
    [Fact]
    public void LoadFromHistory_UnknownEventType_DoesNotThrow()
    {
        var aggregate = new CounterAggregate();
        object[] history = [new UnknownEvent(42)];

        var exception = Record.Exception(() => aggregate.LoadFromHistory(history));

        exception.Should().BeNull();
        aggregate.Ignored.Should().Be(1);
    }

    [Fact]
    public void Raise_KnownEvent_AppendsUncommittedAndAdvancesVersion()
    {
        var aggregate = new CounterAggregate();

        aggregate.Increment();

        aggregate.Version.Should().Be(0);
        aggregate.UncommittedEvents.Should().ContainSingle();
        aggregate.UncommittedEvents[0].Should().BeOfType<Incremented>();
    }

    [Fact]
    public void Raise_AppliesStateBeforeQueueing()
    {
        var aggregate = new CounterAggregate();

        aggregate.Increment();

        // State (Applied) is updated as part of the very same call that queues the event —
        // there is no window where the event is queued but state has not yet folded it in.
        aggregate.Applied.Should().Be(1);
        aggregate.UncommittedEvents.Should().ContainSingle();
    }

    [Fact]
    public void Raise_NullEvent_ThrowsArgumentNullException()
    {
        var aggregate = new CounterAggregate();

        Invoking(aggregate.RaiseNull).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void LoadFromHistory_DoesNotPopulateUncommittedEvents()
    {
        var aggregate = new CounterAggregate();
        object[] history = [new Incremented(), new Incremented(), new Decremented()];

        aggregate.LoadFromHistory(history);

        aggregate.UncommittedEvents.Should().BeEmpty();
    }

    [Fact]
    public void LoadFromHistory_NullHistory_ThrowsArgumentNullException()
    {
        var aggregate = new CounterAggregate();

        Invoking(() => aggregate.LoadFromHistory(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void LoadFromHistory_MultipleEvents_FoldsToLastVersion()
    {
        var aggregate = new CounterAggregate();
        object[] history = [new Incremented(), new Incremented(), new Decremented(), new UnknownEvent(7)];

        aggregate.LoadFromHistory(history);

        aggregate.Version.Should().Be(3);
        aggregate.Applied.Should().Be(1);
        aggregate.Ignored.Should().Be(1);
    }

    [Fact]
    public void ClearUncommittedEvents_EmptiesQueue_KeepsVersion()
    {
        var aggregate = new CounterAggregate();
        aggregate.Increment();
        aggregate.Increment();

        aggregate.ClearUncommittedEvents();

        aggregate.UncommittedEvents.Should().BeEmpty();
        aggregate.Version.Should().Be(1);
    }

    /// <summary>D1 default: an aggregate that never overrides <c>CaptureState</c> reports "snapshotting unsupported".</summary>
    [Fact]
    public void TakeSnapshot_AggregateWithoutOverride_ReturnsNull()
    {
        var aggregate = new CounterAggregate();

        var snapshot = aggregate.TakeSnapshot();

        snapshot.Should().BeNull();
    }

    [Fact]
    public void SnapshotSchemaVersion_AggregateWithoutOverride_DefaultsToZero()
    {
        var aggregate = new CounterAggregate();

        aggregate.SnapshotSchemaVersion.Should().Be(0);
    }

    [Fact]
    public void TakeSnapshot_SnapshotableAggregate_ReturnsCapturedState()
    {
        var aggregate = new SnapshotCounter();
        aggregate.Increment();
        aggregate.Increment();

        var snapshot = aggregate.TakeSnapshot();

        snapshot.Should().NotBeNull();
    }

    [Fact]
    public void RestoreFromSnapshot_FreshAggregate_RestoresCapturedStateAndSetsVersion()
    {
        var writer = new SnapshotCounter();
        writer.Increment();
        writer.Increment();
        var state = writer.TakeSnapshot()!.Value;

        var restored = new SnapshotCounter();
        restored.RestoreFromSnapshot(state, writer.Version);

        restored.Value.Should().Be(writer.Value);
        restored.Version.Should().Be(writer.Version);
    }

    /// <summary>Task 6.1 invariant guard: restoring a snapshot into an aggregate that already has history would silently discard that history, so it throws instead.</summary>
    [Fact]
    public void RestoreFromSnapshot_AlreadyHydratedAggregate_ThrowsInvalidOperationException()
    {
        var aggregate = new SnapshotCounter();
        aggregate.Increment(); // Version becomes 0 — no longer the fresh -1 RestoreFromSnapshot requires.

        Invoking(() => aggregate.RestoreFromSnapshot(new byte[] { 1 }, 0)).Should().Throw<InvalidOperationException>();
    }
}
