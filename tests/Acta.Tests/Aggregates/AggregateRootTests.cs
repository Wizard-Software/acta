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

        Assert.Null(exception);
        Assert.Equal(1, aggregate.Ignored);
    }

    [Fact]
    public void Raise_KnownEvent_AppendsUncommittedAndAdvancesVersion()
    {
        var aggregate = new CounterAggregate();

        aggregate.Increment();

        Assert.Equal(0, aggregate.Version);
        Assert.Single(aggregate.UncommittedEvents);
        Assert.IsType<Incremented>(aggregate.UncommittedEvents[0]);
    }

    [Fact]
    public void Raise_AppliesStateBeforeQueueing()
    {
        var aggregate = new CounterAggregate();

        aggregate.Increment();

        // State (Applied) is updated as part of the very same call that queues the event —
        // there is no window where the event is queued but state has not yet folded it in.
        Assert.Equal(1, aggregate.Applied);
        Assert.Single(aggregate.UncommittedEvents);
    }

    [Fact]
    public void Raise_NullEvent_ThrowsArgumentNullException()
    {
        var aggregate = new CounterAggregate();

        Assert.Throws<ArgumentNullException>(aggregate.RaiseNull);
    }

    [Fact]
    public void LoadFromHistory_DoesNotPopulateUncommittedEvents()
    {
        var aggregate = new CounterAggregate();
        object[] history = [new Incremented(), new Incremented(), new Decremented()];

        aggregate.LoadFromHistory(history);

        Assert.Empty(aggregate.UncommittedEvents);
    }

    [Fact]
    public void LoadFromHistory_NullHistory_ThrowsArgumentNullException()
    {
        var aggregate = new CounterAggregate();

        Assert.Throws<ArgumentNullException>(() => aggregate.LoadFromHistory(null!));
    }

    [Fact]
    public void LoadFromHistory_MultipleEvents_FoldsToLastVersion()
    {
        var aggregate = new CounterAggregate();
        object[] history = [new Incremented(), new Incremented(), new Decremented(), new UnknownEvent(7)];

        aggregate.LoadFromHistory(history);

        Assert.Equal(3, aggregate.Version);
        Assert.Equal(1, aggregate.Applied);
        Assert.Equal(1, aggregate.Ignored);
    }

    [Fact]
    public void ClearUncommittedEvents_EmptiesQueue_KeepsVersion()
    {
        var aggregate = new CounterAggregate();
        aggregate.Increment();
        aggregate.Increment();

        aggregate.ClearUncommittedEvents();

        Assert.Empty(aggregate.UncommittedEvents);
        Assert.Equal(1, aggregate.Version);
    }
}
