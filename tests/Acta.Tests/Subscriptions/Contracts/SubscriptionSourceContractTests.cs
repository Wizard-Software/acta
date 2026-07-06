using Xunit;

using Acta.Abstractions;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Subscriptions.Contracts;

/// <summary>
/// The shared, written-once contract suite for <see cref="ISubscriptionSource"/> (R3 pattern,
/// TESTING-SPEC §5.1). Every backend supplies a fresh (store, source) pair through
/// <see cref="CreateAsync"/> and inherits these facts unchanged: the in-memory backend via
/// <see cref="InMemorySubscriptionSourceContractTests"/> now, the Postgres backend via Feature 7.
/// Tests seed events through the <see cref="IEventStore"/> and read them back through the
/// <see cref="ISubscriptionSource"/>.
/// </summary>
public abstract class SubscriptionSourceContractTests
{
    /// <summary>Produces a fresh, empty store paired with a source over it — backend-specific.</summary>
    protected abstract ValueTask<(IEventStore Store, ISubscriptionSource Source)> CreateAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<StoredEvent>> ToListAsync(IAsyncEnumerable<StoredEvent> source)
    {
        var result = new List<StoredEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    // ---- ReadBatchAsync: ordering, bounds, limit ----

    [Fact]
    public async Task ReadBatch_FromStart_ReturnsEventsOrderedByGlobalPosition()
    {
        var (store, source) = await CreateAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        var batch = await source.ReadBatchAsync(GlobalPosition.Start, maxCount: 10, ct: Ct);

        long[] expectedPositions = [1, 2, 3, 4, 5];
        batch.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
    }

    [Fact]
    public async Task ReadBatch_From_IsExclusiveLowerBound()
    {
        var (store, source) = await CreateAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        var batch = await source.ReadBatchAsync(new GlobalPosition(2), maxCount: 10, ct: Ct);

        long[] expectedPositions = [3, 4, 5];
        batch.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
    }

    [Fact]
    public async Task ReadBatch_MaxCount_LimitsBatchSize()
    {
        var (store, source) = await CreateAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(5), Ct);

        var batch = await source.ReadBatchAsync(GlobalPosition.Start, maxCount: 2, ct: Ct);

        long[] expectedPositions = [1, 2];
        batch.Count.Should().Be(2);
        batch.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
    }

    // ---- ReadBatchAsync: event-type filter pushdown ----

    [Fact]
    public async Task ReadBatch_EventTypesFilter_ReturnsOnlyMatchingTypes()
    {
        var (store, source) = await CreateAsync();
        await store.AppendAsync(
            "order-1",
            ExpectedVersion.NoStream,
            [TestEvents.OrderPlaced(), TestEvents.OrderCancelled(), TestEvents.OrderPlaced()],
            Ct);

        var batch = await source.ReadBatchAsync(
            GlobalPosition.Start,
            maxCount: 10,
            eventTypes: new HashSet<string> { "OrderCancelled" },
            ct: Ct);

        batch.Should().ContainSingle();
        batch[0].EventType.Should().Be("OrderCancelled");
        batch[0].GlobalPosition.Value.Should().Be(2);
    }

    [Fact]
    public async Task ReadBatch_NullEventTypes_ReturnsAllTypes()
    {
        var (store, source) = await CreateAsync();
        await store.AppendAsync(
            "order-1",
            ExpectedVersion.NoStream,
            [TestEvents.OrderPlaced(), TestEvents.OrderCancelled()],
            Ct);

        var batch = await source.ReadBatchAsync(GlobalPosition.Start, maxCount: 10, eventTypes: null, ct: Ct);

        batch.Select(e => e.EventType).Should().Equal("OrderPlaced", "OrderCancelled");
    }

    [Fact]
    public async Task ReadBatch_EmptyEventTypes_ReturnsEmptyBatch()
    {
        var (store, source) = await CreateAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        var batch = await source.ReadBatchAsync(
            GlobalPosition.Start,
            maxCount: 10,
            eventTypes: new HashSet<string>(),
            ct: Ct);

        batch.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadBatch_TypeFilterWithMaxCount_CountsFilteredEvents()
    {
        var (store, source) = await CreateAsync();
        // Interleaved types: OrderPlaced at global positions 1,3,5; OrderCancelled at 2,4,6.
        await store.AppendAsync(
            "order-1",
            ExpectedVersion.NoStream,
            [
                TestEvents.OrderPlaced(), TestEvents.OrderCancelled(),
                TestEvents.OrderPlaced(), TestEvents.OrderCancelled(),
                TestEvents.OrderPlaced(), TestEvents.OrderCancelled(),
            ],
            Ct);

        var batch = await source.ReadBatchAsync(
            GlobalPosition.Start,
            maxCount: 2,
            eventTypes: new HashSet<string> { "OrderPlaced" },
            ct: Ct);

        // maxCount counts MATCHING events, scanning past the interleaved non-matches: the two
        // OrderPlaced events at positions 1 and 3 — NOT position 1 alone (which a filter-after-Take
        // implementation would wrongly return).
        long[] expectedPositions = [1, 3];
        batch.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
        batch.Should().OnlyContain(e => e.EventType == "OrderPlaced");
    }

    // ---- ReadBatchAsync: HWM visibility + empty batch ----

    [Fact]
    public async Task ReadBatch_FreshlyAppendedEvent_IsImmediatelyVisible()
    {
        // Single-process backends have zero visibility-lag cutback (safe HWM = head), so a
        // just-committed event is readable at once (ADR-015).
        var (store, source) = await CreateAsync();

        var appended = await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);
        var batch = await source.ReadBatchAsync(GlobalPosition.Start, maxCount: 10, ct: Ct);

        batch.Should().ContainSingle();
        batch[0].GlobalPosition.Should().Be(appended.LastGlobalPosition);
    }

    [Fact]
    public async Task ReadBatch_NoSafeEventsAfterCheckpoint_ReturnsEmpty()
    {
        var (store, source) = await CreateAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        // from = head: nothing beyond the last consumed position.
        var batch = await source.ReadBatchAsync(new GlobalPosition(3), maxCount: 10, ct: Ct);

        batch.Should().BeEmpty();
    }

    // ---- ReadFromAsync (live streaming path) ----

    [Fact]
    public async Task ReadFrom_StreamsEventsAfterCheckpointInOrder()
    {
        var (store, source) = await CreateAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        var streamed = await ToListAsync(source.ReadFromAsync(new GlobalPosition(2), Ct));

        long[] expectedPositions = [3, 4, 5];
        streamed.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
    }
}
