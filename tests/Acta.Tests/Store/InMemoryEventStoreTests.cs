using Xunit;

using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Store;

public sealed class InMemoryEventStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    private static EventData CreateEventData(string eventType = "TestEvent") =>
        new(Guid.NewGuid(), eventType, 1, new byte[] { 1, 2, 3 }, CreateMetadata());

    private static EventData[] CreateBatch(int count, string eventType = "TestEvent") =>
        [.. Enumerable.Range(0, count).Select(_ => CreateEventData(eventType))];

    private static async Task<List<StoredEvent>> ToListAsync(IAsyncEnumerable<StoredEvent> source)
    {
        var result = new List<StoredEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }

    [Fact]
    public async Task AppendAsync_NewStream_AssignsSequentialVersionsAndGlobalPositions()
    {
        var store = new InMemoryEventStore();
        var batch = CreateBatch(3);

        var result = await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        Assert.Equal(2, result.NextExpectedVersion);
        Assert.Equal(3, result.LastGlobalPosition.Value);
        Assert.False(result.Deduplicated);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));
        long[] expectedVersions = [0, 1, 2];
        long[] expectedPositions = [1, 2, 3];
        Assert.Equal(3, stored.Count);
        Assert.Equal(expectedVersions, stored.Select(e => e.Version));
        Assert.Equal(expectedPositions, stored.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task AppendAsync_ExactVersionMatch_Succeeds()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);

        var result = await store.AppendAsync("order-1", 1, CreateBatch(1), Ct);

        Assert.Equal(2, result.NextExpectedVersion);
        Assert.False(result.Deduplicated);
    }

    [Fact]
    public async Task AppendAsync_ExactVersionMismatch_ThrowsConcurrencyExceptionWithActualVersion()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.AppendAsync("order-1", 5, CreateBatch(1), Ct).AsTask());

        Assert.Equal("order-1", ex.StreamId);
        Assert.Equal(5, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task AppendAsync_NoStreamToExistingStream_ThrowsConcurrencyException()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct).AsTask());
    }

    [Fact]
    public async Task AppendAsync_StreamExistsToMissingStream_ThrowsConcurrencyException()
    {
        var store = new InMemoryEventStore();

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.AppendAsync("order-1", ExpectedVersion.StreamExists, CreateBatch(1), Ct).AsTask());
    }

    [Fact]
    public async Task AppendAsync_StreamExistsToExistingStream_Succeeds()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);

        var result = await store.AppendAsync("order-1", ExpectedVersion.StreamExists, CreateBatch(1), Ct);

        Assert.Equal(1, result.NextExpectedVersion);
    }

    [Fact]
    public async Task AppendAsync_AnyOnConflictingVersion_DoesNotThrow()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);

        var result = await store.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct);

        Assert.Equal(2, result.NextExpectedVersion);
    }

    [Fact]
    public async Task AppendAsync_FullBatchDuplicate_ReturnsDeduplicatedSuccessWithoutAppending()
    {
        var store = new InMemoryEventStore();
        var batch = CreateBatch(2);
        var first = await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        var replay = await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        Assert.True(replay.Deduplicated);
        Assert.Equal(first.NextExpectedVersion, replay.NextExpectedVersion);
        Assert.Equal(first.LastGlobalPosition, replay.LastGlobalPosition);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public async Task AppendAsync_DuplicateUnderAny_ReturnsDeduplicatedSuccess()
    {
        var store = new InMemoryEventStore();
        var batch = CreateBatch(1);
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        var replay = await store.AppendAsync("order-1", ExpectedVersion.Any, batch, Ct);

        Assert.True(replay.Deduplicated);
    }

    [Fact]
    public async Task AppendAsync_RetryAfterSuccessWithStaleExpectedVersion_DoesNotThrow()
    {
        var store = new InMemoryEventStore();
        var batch = CreateBatch(1);
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        // Someone else appended meanwhile, advancing the stream past what the first command saw.
        await store.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct);

        // Retry of the FIRST command with its original (now stale) expectedVersion = NoStream —
        // full-batch dedup must be recognized BEFORE the guard, so this must not throw (ADR-003).
        var replay = await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        Assert.True(replay.Deduplicated);
    }

    [Fact]
    public async Task ReadStreamAsync_Forwards_ReturnsEventsInAscendingVersionOrder()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));

        long[] expectedVersions = [0, 1, 2];
        Assert.Equal(expectedVersions, stored.Select(e => e.Version));
    }

    [Fact]
    public async Task ReadStreamAsync_Backwards_ReturnsEventsInDescendingVersionOrder()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", direction: Direction.Backwards, ct: Ct));

        long[] expectedVersions = [2, 1, 0];
        Assert.Equal(expectedVersions, stored.Select(e => e.Version));
    }

    [Fact]
    public async Task ReadStreamAsync_FromToRange_ReturnsPointInTimeSlice()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(5), Ct);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", fromVersion: 1, toVersion: 3, ct: Ct));

        long[] expectedVersions = [1, 2, 3];
        Assert.Equal(expectedVersions, stored.Select(e => e.Version));
    }

    [Fact]
    public async Task ReadStreamAsync_NonExistentStream_ReturnsEmpty()
    {
        var store = new InMemoryEventStore();

        var stored = await ToListAsync(store.ReadStreamAsync("ghost", ct: Ct));

        Assert.Empty(stored);
    }

    [Fact]
    public async Task ReadAllAsync_FromStart_ReturnsAllEventsAcrossStreams()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, ct: Ct));

        long[] expectedPositions = [1, 2, 3, 4, 5];
        Assert.Equal(5, all.Count);
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAllAsync_UpTo_ReturnsPointInTimeSlice()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(4), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, upTo: new GlobalPosition(2), ct: Ct));

        long[] expectedPositions = [1, 2];
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAllAsync_MaxCount_LimitsBatchSize()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(5), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, maxCount: 2, ct: Ct));

        long[] expectedPositions = [1, 2];
        Assert.Equal(2, all.Count);
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAllAsync_Backwards_ReturnsDescendingGlobalPositionOrder()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, direction: Direction.Backwards, ct: Ct));

        long[] expectedPositions = [3, 2, 1];
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAllAsync_FromNonStartExclusiveLowerBound_ExcludesEventsAtAndBelowFrom()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var all = await ToListAsync(store.ReadAllAsync(new GlobalPosition(2), ct: Ct));

        long[] expectedPositions = [3, 4, 5];
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task ReadAllAsync_BackwardsWithMaxCount_ReturnsHighestPositionsDescending()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var all = await ToListAsync(
            store.ReadAllAsync(GlobalPosition.Start, maxCount: 2, direction: Direction.Backwards, ct: Ct));

        long[] expectedPositions = [5, 4];
        Assert.Equal(2, all.Count);
        Assert.Equal(expectedPositions, all.Select(e => e.GlobalPosition.Value));
    }

    [Fact]
    public async Task AppendAsync_MultipleStreams_GlobalPositionIsMonotonicAcrossStreams()
    {
        var store = new InMemoryEventStore();

        var first = await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        var second = await store.AppendAsync("order-2", ExpectedVersion.NoStream, CreateBatch(1), Ct);

        // NB: expectedVersion 0 is reserved as the EmptyStream sentinel (never "exact version
        // 0" — MODULE-INTERFACES: exact versions start at 1), so a further append to a
        // one-event stream must use Any/StreamExists/exact->=1, not the literal 0.
        var third = await store.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct);

        Assert.True(first.LastGlobalPosition < second.LastGlobalPosition);
        Assert.True(second.LastGlobalPosition < third.LastGlobalPosition);
    }

    [Fact]
    public async Task AppendAsync_ParallelAppendsToSameStream_NoLostEventsAndContiguousVersions()
    {
        var store = new InMemoryEventStore();
        const int parallelAppends = 32;
        var ct = Ct;

        var tasks = Enumerable.Range(0, parallelAppends)
            .Select(_ => Task.Run(async () => await store.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), ct)))
            .ToArray();
        await Task.WhenAll(tasks);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: ct));

        Assert.Equal(parallelAppends, stored.Count);
        long[] expectedVersions = [.. Enumerable.Range(0, parallelAppends).Select(i => (long)i)];
        Assert.Equal(expectedVersions, stored.Select(e => e.Version));

        var globalPositions = stored.Select(e => e.GlobalPosition.Value).ToArray();
        Assert.Equal(globalPositions.Distinct().Count(), globalPositions.Length);
        Assert.Equal(globalPositions.OrderBy(v => v), globalPositions);
    }

    [Fact]
    public async Task AppendAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), cts.Token).AsTask());
    }

    [Fact]
    public async Task ReadStreamAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in store.ReadStreamAsync("order-1", ct: cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task ReadAllAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in store.ReadAllAsync(GlobalPosition.Start, ct: cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task AppendAsync_EmptyBatch_ReturnsNoOpSuccessWithoutChangingState()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);

        var result = await store.AppendAsync("order-1", ExpectedVersion.Any, [], Ct);

        Assert.False(result.Deduplicated);
        Assert.Equal(1, result.NextExpectedVersion);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public async Task AppendAsync_EmptyStreamGuardOnNewStream_Succeeds()
    {
        var store = new InMemoryEventStore();

        var result = await store.AppendAsync("order-1", ExpectedVersion.EmptyStream, CreateBatch(1), Ct);

        Assert.Equal(0, result.NextExpectedVersion);
    }

    [Fact]
    public async Task AppendAsync_NullStreamId_ThrowsArgumentNullException()
    {
        var store = new InMemoryEventStore();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.AppendAsync(null!, ExpectedVersion.Any, CreateBatch(1), Ct).AsTask());
    }

    [Fact]
    public async Task AppendAsync_EmptyStreamId_ThrowsArgumentException()
    {
        var store = new InMemoryEventStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.AppendAsync(string.Empty, ExpectedVersion.Any, CreateBatch(1), Ct).AsTask());
    }

    [Fact]
    public async Task AppendAsync_NullEvents_ThrowsArgumentNullException()
    {
        var store = new InMemoryEventStore();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.AppendAsync("order-1", ExpectedVersion.Any, null!, Ct).AsTask());
    }
}
