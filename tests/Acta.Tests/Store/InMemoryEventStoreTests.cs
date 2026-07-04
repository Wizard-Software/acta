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

    /// <summary>A <see cref="TimeProvider"/> that always reports a fixed, caller-supplied instant.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task AppendAsync_NewStream_AssignsSequentialVersionsAndGlobalPositions()
    {
        var store = new InMemoryEventStore();
        var batch = CreateBatch(3);

        var result = await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        result.NextExpectedVersion.Should().Be(2);
        result.LastGlobalPosition.Value.Should().Be(3);
        result.Deduplicated.Should().BeFalse();

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));
        long[] expectedVersions = [0, 1, 2];
        long[] expectedPositions = [1, 2, 3];
        stored.Count.Should().Be(3);
        stored.Select(e => e.Version).Should().Equal(expectedVersions);
        stored.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
    }

    [Fact]
    public async Task AppendAsync_ExactVersionMatch_Succeeds()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);

        var result = await store.AppendAsync("order-1", 1, CreateBatch(1), Ct);

        result.NextExpectedVersion.Should().Be(2);
        result.Deduplicated.Should().BeFalse();
    }

    [Fact]
    public async Task AppendAsync_ExactVersionMismatch_ThrowsConcurrencyExceptionWithActualVersion()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);

        var ex = (await Awaiting(
            () => store.AppendAsync("order-1", 5, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<ConcurrencyException>()).Which;

        ex.StreamId.Should().Be("order-1");
        ex.ExpectedVersion.Should().Be(5);
        ex.ActualVersion.Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_NoStreamToExistingStream_ThrowsConcurrencyException()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);

        await Awaiting(
            () => store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<ConcurrencyException>();
    }

    [Fact]
    public async Task AppendAsync_StreamExistsToMissingStream_ThrowsConcurrencyException()
    {
        var store = new InMemoryEventStore();

        await Awaiting(
            () => store.AppendAsync("order-1", ExpectedVersion.StreamExists, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<ConcurrencyException>();
    }

    [Fact]
    public async Task AppendAsync_StreamExistsToExistingStream_Succeeds()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);

        var result = await store.AppendAsync("order-1", ExpectedVersion.StreamExists, CreateBatch(1), Ct);

        result.NextExpectedVersion.Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_AnyOnConflictingVersion_DoesNotThrow()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);

        var result = await store.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct);

        result.NextExpectedVersion.Should().Be(2);
    }

    [Fact]
    public async Task AppendAsync_FullBatchDuplicate_ReturnsDeduplicatedSuccessWithoutAppending()
    {
        var store = new InMemoryEventStore();
        var batch = CreateBatch(2);
        var first = await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        var replay = await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        replay.Deduplicated.Should().BeTrue();
        replay.NextExpectedVersion.Should().Be(first.NextExpectedVersion);
        replay.LastGlobalPosition.Should().Be(first.LastGlobalPosition);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));
        stored.Count.Should().Be(2);
    }

    [Fact]
    public async Task AppendAsync_DuplicateUnderAny_ReturnsDeduplicatedSuccess()
    {
        var store = new InMemoryEventStore();
        var batch = CreateBatch(1);
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, batch, Ct);

        var replay = await store.AppendAsync("order-1", ExpectedVersion.Any, batch, Ct);

        replay.Deduplicated.Should().BeTrue();
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

        replay.Deduplicated.Should().BeTrue();
    }

    [Fact]
    public async Task ReadStreamAsync_Forwards_ReturnsEventsInAscendingVersionOrder()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));

        long[] expectedVersions = [0, 1, 2];
        stored.Select(e => e.Version).Should().Equal(expectedVersions);
    }

    [Fact]
    public async Task ReadStreamAsync_Backwards_ReturnsEventsInDescendingVersionOrder()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", direction: Direction.Backwards, ct: Ct));

        long[] expectedVersions = [2, 1, 0];
        stored.Select(e => e.Version).Should().Equal(expectedVersions);
    }

    [Fact]
    public async Task ReadStreamAsync_FromToRange_ReturnsPointInTimeSlice()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(5), Ct);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", fromVersion: 1, toVersion: 3, ct: Ct));

        long[] expectedVersions = [1, 2, 3];
        stored.Select(e => e.Version).Should().Equal(expectedVersions);
    }

    [Fact]
    public async Task ReadStreamAsync_NonExistentStream_ReturnsEmpty()
    {
        var store = new InMemoryEventStore();

        var stored = await ToListAsync(store.ReadStreamAsync("ghost", ct: Ct));

        stored.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAllAsync_FromStart_ReturnsAllEventsAcrossStreams()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, ct: Ct));

        long[] expectedPositions = [1, 2, 3, 4, 5];
        all.Count.Should().Be(5);
        all.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
    }

    [Fact]
    public async Task ReadAllAsync_UpTo_ReturnsPointInTimeSlice()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(4), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, upTo: new GlobalPosition(2), ct: Ct));

        long[] expectedPositions = [1, 2];
        all.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
    }

    [Fact]
    public async Task ReadAllAsync_MaxCount_LimitsBatchSize()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(5), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, maxCount: 2, ct: Ct));

        long[] expectedPositions = [1, 2];
        all.Count.Should().Be(2);
        all.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
    }

    [Fact]
    public async Task ReadAllAsync_Backwards_ReturnsDescendingGlobalPositionOrder()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, direction: Direction.Backwards, ct: Ct));

        long[] expectedPositions = [3, 2, 1];
        all.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
    }

    [Fact]
    public async Task ReadAllAsync_FromNonStartExclusiveLowerBound_ExcludesEventsAtAndBelowFrom()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
        await store.AppendAsync("order-2", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        var all = await ToListAsync(store.ReadAllAsync(new GlobalPosition(2), ct: Ct));

        long[] expectedPositions = [3, 4, 5];
        all.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
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
        all.Count.Should().Be(2);
        all.Select(e => e.GlobalPosition.Value).Should().Equal(expectedPositions);
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

        (first.LastGlobalPosition < second.LastGlobalPosition).Should().BeTrue();
        (second.LastGlobalPosition < third.LastGlobalPosition).Should().BeTrue();
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

        stored.Count.Should().Be(parallelAppends);
        long[] expectedVersions = [.. Enumerable.Range(0, parallelAppends).Select(i => (long)i)];
        stored.Select(e => e.Version).Should().Equal(expectedVersions);

        var globalPositions = stored.Select(e => e.GlobalPosition.Value).ToArray();
        globalPositions.Length.Should().Be(globalPositions.Distinct().Count());
        globalPositions.Should().Equal(globalPositions.OrderBy(v => v));
    }

    [Fact]
    public async Task AppendAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(
            () => store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), cts.Token).AsTask()).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadStreamAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(async () =>
        {
            await foreach (var _ in store.ReadStreamAsync("order-1", ct: cts.Token))
            {
            }
        }).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadAllAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(async () =>
        {
            await foreach (var _ in store.ReadAllAsync(GlobalPosition.Start, ct: cts.Token))
            {
            }
        }).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AppendAsync_EmptyBatch_ReturnsNoOpSuccessWithoutChangingState()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);

        var result = await store.AppendAsync("order-1", ExpectedVersion.Any, [], Ct);

        result.Deduplicated.Should().BeFalse();
        result.NextExpectedVersion.Should().Be(1);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));
        stored.Count.Should().Be(2);
    }

    [Fact]
    public async Task AppendAsync_EmptyStreamGuardOnNewStream_Succeeds()
    {
        var store = new InMemoryEventStore();

        var result = await store.AppendAsync("order-1", ExpectedVersion.EmptyStream, CreateBatch(1), Ct);

        result.NextExpectedVersion.Should().Be(0);
    }

    [Fact]
    public async Task AppendAsync_NullStreamId_ThrowsArgumentNullException()
    {
        var store = new InMemoryEventStore();

        await Awaiting(
            () => store.AppendAsync(null!, ExpectedVersion.Any, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AppendAsync_EmptyStreamId_ThrowsArgumentException()
    {
        var store = new InMemoryEventStore();

        await Awaiting(
            () => store.AppendAsync(string.Empty, ExpectedVersion.Any, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AppendAsync_NullEvents_ThrowsArgumentNullException()
    {
        var store = new InMemoryEventStore();

        await Awaiting(
            () => store.AppendAsync("order-1", ExpectedVersion.Any, null!, Ct).AsTask()).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Constructor_ExplicitTimeProvider_IsUsedForStoredEventTimestamps()
    {
        var fixedTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new InMemoryEventStore(new FixedTimeProvider(fixedTime));

        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);

        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));
        stored[0].Timestamp.Should().Be(fixedTime);
    }

    [Fact]
    public void ReadStreamAsync_NullStreamId_ThrowsArgumentNullException()
    {
        var store = new InMemoryEventStore();

        Invoking(() => store.ReadStreamAsync(null!, ct: Ct)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadAllAsync_CancelledTokenOnEmptyStore_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // No events exist, so the foreach body (and its own cancellation check) never runs —
        // this can only throw via the upfront check made before the store snapshot is even read.
        await Awaiting(async () =>
        {
            await foreach (var _ in store.ReadAllAsync(GlobalPosition.Start, ct: cts.Token))
            {
            }
        }).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadAllAsync_TokenCancelledAfterFirstItem_ThrowsOperationCanceledExceptionBeforeSecondItem()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
        using var cts = new CancellationTokenSource();

        // The upfront cancellation check passes (token not yet cancelled), so only the
        // per-iteration check inside the read loop can observe the mid-enumeration cancellation.
        await Awaiting(async () =>
        {
            await foreach (var _ in store.ReadAllAsync(GlobalPosition.Start, ct: cts.Token))
            {
                cts.Cancel();
            }
        }).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadStreamAsync_CancelledTokenOnNonExistentStream_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The stream has no events, so the foreach body (and its own cancellation check) never
        // runs — this can only throw via the upfront check made before the stream lookup.
        await Awaiting(async () =>
        {
            await foreach (var _ in store.ReadStreamAsync("ghost", ct: cts.Token))
            {
            }
        }).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadStreamAsync_TokenCancelledAfterFirstItem_ThrowsOperationCanceledExceptionBeforeSecondItem()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
        using var cts = new CancellationTokenSource();

        // The upfront cancellation check passes (token not yet cancelled), so only the
        // per-iteration check inside the read loop can observe the mid-enumeration cancellation.
        await Awaiting(async () =>
        {
            await foreach (var _ in store.ReadStreamAsync("order-1", ct: cts.Token))
            {
                cts.Cancel();
            }
        }).Should().ThrowAsync<OperationCanceledException>();
    }
}
