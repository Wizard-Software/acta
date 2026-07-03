using Xunit;

using Acta.Abstractions;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Store.Contracts;

/// <summary>
/// The shared, written-once contract suite for <see cref="IEventStore"/> (R3 pattern,
/// TESTING-SPEC §5.1). Every backend supplies a fresh store through <see cref="CreateStoreAsync"/>
/// and inherits these facts unchanged: the in-memory backend via
/// <see cref="InMemoryEventStoreContractTests"/> now, the Postgres backend via task 7.2.
/// <para>
/// Coverage (per the 2.2 scope): the full dedup-guarantee matrix per <c>ExpectedVersion</c> mode
/// (03-contracts.md §1, ADR-003) and the global-read guard of <see cref="IEventStore.ReadAllAsync"/>
/// (from/upTo/maxCount/direction — ADR-015). Partially-overlapping batches (GAP-1) belong to the
/// 2.3 property tests; the <c>EmptyStream</c>-on-an-existing-empty-stream cell (GAP-2) is
/// unconstructable in-memory and is deferred to the Postgres backend (7.2).
/// </para>
/// </summary>
public abstract class EventStoreContractTests
{
    /// <summary>Produces a fresh, empty store for a single test — backend-specific.</summary>
    protected abstract ValueTask<IEventStore> CreateStoreAsync();

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

    // ---- Smoke: append round-trips through a stream read (version assignment) ----

    [Fact]
    public async Task Smoke_AppendThenReadStream_RoundTripsWithSequentialVersions()
    {
        var store = await CreateStoreAsync();

        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));
        long[] expectedVersions = [0, 1, 2];
        Assert.Equal(3, stored.Count);
        Assert.Equal(expectedVersions, stored.Select(e => e.Version));
    }

    // ---- Matrix: exact version (>= 1) ----

    [Fact]
    public async Task ExactVersion_Match_Succeeds()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct); // last version 1

        var result = await store.AppendAsync("order-1", 1, TestEvents.Distinct(1), Ct);

        Assert.Equal(2, result.NextExpectedVersion);
        Assert.False(result.Deduplicated);
    }

    [Fact]
    public async Task ExactVersion_Mismatch_ThrowsConcurrencyExceptionCarryingVersions()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct); // last version 1

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.AppendAsync("order-1", 5, TestEvents.Distinct(1), Ct).AsTask());

        Assert.Equal("order-1", ex.StreamId);
        Assert.Equal(5, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task ExactVersion_DuplicateEventId_ReturnsDeduplicatedEvenWhenGuardWouldFail()
    {
        var store = await CreateStoreAsync();
        var e = TestEvents.OrderPlaced();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, [e], Ct); // version 0

        // Same EventId under an exact guard that would otherwise mismatch: dedup wins (before guard).
        var replay = await store.AppendAsync("order-1", 99, [e], Ct);

        Assert.True(replay.Deduplicated);
    }

    [Fact]
    public async Task ExactVersion_RetryWithStaleExpectedVersion_ReturnsIdempotentSuccess()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct); // last version 1
        var e = TestEvents.OrderPlaced();
        await store.AppendAsync("order-1", 1, [e], Ct);                                            // version 2
        await store.AppendAsync("order-1", ExpectedVersion.Any, TestEvents.Distinct(1), Ct);       // stream moves to 3

        // Retry of the command with its original (now stale) exact expectedVersion = 1 → idempotent.
        var replay = await store.AppendAsync("order-1", 1, [e], Ct);

        Assert.True(replay.Deduplicated);
        // A dedup reports the stream's CURRENT head (no new event appended), not the original
        // append's version — AppendResult XML-doc; the head has since advanced to 3.
        Assert.Equal(3, replay.NextExpectedVersion);
    }

    // ---- Matrix: Any ----

    [Fact]
    public async Task Any_OnConflictingVersion_DoesNotThrow()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);

        var result = await store.AppendAsync("order-1", ExpectedVersion.Any, TestEvents.Distinct(1), Ct);

        Assert.Equal(2, result.NextExpectedVersion);
        Assert.False(result.Deduplicated);
    }

    [Fact]
    public async Task Any_DuplicateEventId_ReturnsDeduplicated()
    {
        var store = await CreateStoreAsync();
        var e = TestEvents.OrderPlaced();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, [e], Ct);

        var replay = await store.AppendAsync("order-1", ExpectedVersion.Any, [e], Ct);

        Assert.True(replay.Deduplicated);
    }

    [Fact]
    public async Task Any_NewEventId_AppendsRealEvent()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, [TestEvents.OrderPlaced()], Ct); // version 0

        var result = await store.AppendAsync("order-1", ExpectedVersion.Any, [TestEvents.OrderPlaced()], Ct);

        Assert.False(result.Deduplicated);
        Assert.Equal(1, result.NextExpectedVersion);
    }

    // ---- Matrix: NoStream ----

    [Fact]
    public async Task NoStream_NewStream_Succeeds()
    {
        var store = await CreateStoreAsync();

        var result = await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);

        Assert.Equal(0, result.NextExpectedVersion);
        Assert.False(result.Deduplicated);
    }

    [Fact]
    public async Task NoStream_ExistingStream_ThrowsConcurrency()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct); // version 0

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct).AsTask());

        Assert.Equal(ExpectedVersion.NoStream, ex.ExpectedVersion);
        Assert.Equal(0, ex.ActualVersion);
    }

    [Fact]
    public async Task NoStream_StaleRetryDuplicate_ReturnsDeduplicatedBeforeGuard()
    {
        var store = await CreateStoreAsync();
        var e = TestEvents.OrderPlaced();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, [e], Ct);
        await store.AppendAsync("order-1", ExpectedVersion.Any, TestEvents.Distinct(1), Ct); // stream now exists past head

        // Retry of the first command with its original NoStream guard (now stale) → dedup before guard.
        var replay = await store.AppendAsync("order-1", ExpectedVersion.NoStream, [e], Ct);

        Assert.True(replay.Deduplicated);
    }

    // ---- Matrix: EmptyStream (GAP-2: existing-empty-stream cell deferred to Postgres 7.2) ----

    [Fact]
    public async Task EmptyStream_NewStream_Succeeds_NextVersionZero()
    {
        var store = await CreateStoreAsync();

        var result = await store.AppendAsync("order-1", ExpectedVersion.EmptyStream, TestEvents.Distinct(1), Ct);

        Assert.Equal(0, result.NextExpectedVersion);
        Assert.False(result.Deduplicated);
    }

    [Fact]
    public async Task EmptyStream_NonEmptyStream_ThrowsConcurrency()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct); // non-empty, last version 1

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.AppendAsync("order-1", ExpectedVersion.EmptyStream, TestEvents.Distinct(1), Ct).AsTask());

        Assert.Equal(ExpectedVersion.EmptyStream, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task EmptyStream_DuplicateEventId_ReturnsDeduplicated()
    {
        var store = await CreateStoreAsync();
        var e = TestEvents.OrderPlaced();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, [e], Ct);

        var replay = await store.AppendAsync("order-1", ExpectedVersion.EmptyStream, [e], Ct);

        Assert.True(replay.Deduplicated);
    }

    // ---- Matrix: StreamExists ----

    [Fact]
    public async Task StreamExists_MissingStream_ThrowsConcurrency()
    {
        var store = await CreateStoreAsync();

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.AppendAsync("order-1", ExpectedVersion.StreamExists, TestEvents.Distinct(1), Ct).AsTask());

        Assert.Equal(ExpectedVersion.StreamExists, ex.ExpectedVersion);
        Assert.Equal(-1, ex.ActualVersion);
    }

    [Fact]
    public async Task StreamExists_ExistingStream_Succeeds()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);

        var result = await store.AppendAsync("order-1", ExpectedVersion.StreamExists, TestEvents.Distinct(1), Ct);

        Assert.Equal(1, result.NextExpectedVersion);
        Assert.False(result.Deduplicated);
    }

    [Fact]
    public async Task StreamExists_DuplicateEventId_ReturnsDeduplicated()
    {
        var store = await CreateStoreAsync();
        var e = TestEvents.OrderPlaced();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, [e], Ct);

        var replay = await store.AppendAsync("order-1", ExpectedVersion.StreamExists, [e], Ct);

        Assert.True(replay.Deduplicated);
    }

    // ---- Canonical TESTING-SPEC §5.1 example ----

    [Fact]
    public async Task AppendAsync_DuplicateEventId_ReturnsDeduplicatedSuccess()
    {
        var store = await CreateStoreAsync();
        var e = TestEvents.OrderPlaced();                                   // stable EventId (same instance)
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, [e], Ct);

        var result = await store.AppendAsync("order-1", ExpectedVersion.Any, [e], Ct);

        Assert.True(result.Deduplicated);                                   // idempotent success — ADR-003
    }

    // ---- Global-read guard (ADR-015) ----

    [Fact]
    public async Task ReadAll_FromStart_ReturnsAllEventsOrderedByGlobalPosition()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, ct: Ct));

        long[] expectedPositions = [1, 2, 3, 4, 5];
        Assert.Equal(5, all.Count);
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAll_From_IsExclusiveLowerBound()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        var all = await ToListAsync(store.ReadAllAsync(new GlobalPosition(2), ct: Ct));

        long[] expectedPositions = [3, 4, 5];
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAll_UpTo_IsInclusivePointInTime()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(4), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, upTo: new GlobalPosition(2), ct: Ct));

        long[] expectedPositions = [1, 2];
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAll_MaxCount_LimitsBatchSize()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(5), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, maxCount: 2, ct: Ct));

        long[] expectedPositions = [1, 2];
        Assert.Equal(2, all.Count);
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAll_Backwards_ReturnsDescendingGlobalPositionOrder()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, direction: Direction.Backwards, ct: Ct));

        long[] expectedPositions = [3, 2, 1];
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAll_BackwardsWithMaxCount_ReturnsHighestPositionsDescending()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);

        var all = await ToListAsync(
            store.ReadAllAsync(GlobalPosition.Start, maxCount: 2, direction: Direction.Backwards, ct: Ct));

        long[] expectedPositions = [5, 4];
        Assert.Equal(2, all.Count);
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAll_FreshlyAppendedEvent_IsImmediatelyVisible()
    {
        // Single-process backends have no visibility-lag cutback: the safe high-water mark is the
        // head, so a just-committed event is readable at once. The guard must not hide it (ADR-015).
        var store = await CreateStoreAsync();

        var appended = await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);
        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, ct: Ct));

        Assert.Single(all);
        Assert.Equal(appended.LastGlobalPosition.Value, all[0].GlobalPosition.Value);
    }
}
