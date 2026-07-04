using Xunit;

using Acta.Abstractions;
using Acta.InMemory;
using Acta.Serialization;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Aggregates;

/// <summary>
/// Unit tests for <c>AggregateRepository{TAggregate}</c> (task 3.2): read/fold via
/// <c>GetByIdAsync</c>/<c>FetchForWritingAsync</c>, write via <c>SaveAsync</c> with an explicit
/// optimistic-concurrency guard, deterministic per-event <c>EventId</c> (idempotent retries — D3),
/// and the read-path allow-list defense (<see cref="UnknownEventTypeException"/> — SEC-2).
/// <para>
/// Several tests build TWO repository instances over the SAME underlying store
/// (<see cref="CounterEventsRegistry.CreateRepository"/> called twice) to model two genuinely
/// distinct commands against one stream: each call gets its own fixed <c>MessageId</c>, so the
/// deterministic <c>EventId</c> derivation (keyed on <c>MessageId</c> + stream id + batch index)
/// does not collide across them. Reusing the SAME repository instance for two <c>SaveAsync</c>
/// calls, by contrast, models a RETRY of the exact same command (see the idempotency test below).
/// </para>
/// </summary>
public sealed class AggregateRepositoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

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
    public async Task GetByIdAsync_NonexistentStream_ReturnsNull()
    {
        var repository = CounterEventsRegistry.CreateRepository();

        var aggregate = await repository.GetByIdAsync("counter-ghost", Ct);

        Assert.Null(aggregate);
    }

    [Fact]
    public async Task GetByIdAsync_SingleEvent_ReturnsAggregateAtVersionZeroWithFoldedState()
    {
        var store = new InMemoryEventStore();
        var repository = CounterEventsRegistry.CreateRepository(store);
        var writer = new CounterAggregate();
        writer.AssignId("counter-1");
        writer.Increment();
        await repository.SaveAsync(writer, ExpectedVersion.NoStream, Ct);

        var aggregate = await repository.GetByIdAsync("counter-1", Ct);

        Assert.NotNull(aggregate);
        Assert.Equal(0, aggregate.Version);
        Assert.Equal(1, aggregate.Applied);
    }

    [Fact]
    public async Task GetByIdAsync_MultipleEvents_FoldsInStreamOrderWithCorrectVersion()
    {
        var store = new InMemoryEventStore();
        var repository = CounterEventsRegistry.CreateRepository(store);
        var writer = new CounterAggregate();
        writer.AssignId("counter-1");
        writer.Increment();
        writer.Increment();
        writer.Decrement();
        await repository.SaveAsync(writer, ExpectedVersion.NoStream, Ct);

        var aggregate = await repository.GetByIdAsync("counter-1", Ct);

        Assert.NotNull(aggregate);
        Assert.Equal(2, aggregate.Version);
        Assert.Equal(1, aggregate.Applied);
    }

    [Fact]
    public async Task GetByIdAsync_RegisteredButUnrecognizedEventType_AbsorbedByTotalApplyWithoutThrowing()
    {
        var store = new InMemoryEventStore();
        var appendSerializer = CounterEventsRegistry.CreateSerializer();
        var eventData = appendSerializer.ToEventData(new UnknownEvent(42), CreateMetadata(), Guid.NewGuid());
        await store.AppendAsync("counter-1", ExpectedVersion.NoStream, [eventData], Ct);
        var repository = CounterEventsRegistry.CreateRepository(store);

        CounterAggregate? aggregate = null;
        var exception = await Record.ExceptionAsync(async () => aggregate = await repository.GetByIdAsync("counter-1", Ct));

        Assert.Null(exception);
        Assert.NotNull(aggregate);
        Assert.Equal(1, aggregate.Ignored);
        Assert.Equal(0, aggregate.Applied);
    }

    [Fact]
    public async Task GetByIdAsync_UnregisteredEventTypeInStream_ThrowsUnknownEventTypeException()
    {
        var store = new InMemoryEventStore();
        var eventData = new EventData(Guid.NewGuid(), "TotallyUnregisteredEvent", 1, new byte[] { 1, 2, 3 }, CreateMetadata());
        await store.AppendAsync("counter-1", ExpectedVersion.NoStream, [eventData], Ct);
        var repository = CounterEventsRegistry.CreateRepository(store);

        await Assert.ThrowsAsync<UnknownEventTypeException>(() => repository.GetByIdAsync("counter-1", Ct).AsTask());
    }

    [Fact]
    public async Task FetchForWritingAsync_EmptyStream_ReturnsFreshAggregateWithReadVersionMinusOne()
    {
        var repository = CounterEventsRegistry.CreateRepository();

        var session = await repository.FetchForWritingAsync("counter-new", Ct);

        Assert.Equal(-1, session.ReadVersion);
        Assert.Equal(-1, session.Aggregate.Version);
        Assert.Empty(session.Aggregate.UncommittedEvents);
    }

    [Fact]
    public async Task FetchForWritingAsync_ExistingStream_ReturnsHydratedAggregateWithReadVersionAtLastEventVersion()
    {
        var store = new InMemoryEventStore();
        var repository = CounterEventsRegistry.CreateRepository(store);
        var writer = new CounterAggregate();
        writer.AssignId("counter-1");
        writer.Increment();
        writer.Increment();
        await repository.SaveAsync(writer, ExpectedVersion.NoStream, Ct);

        var session = await repository.FetchForWritingAsync("counter-1", Ct);

        Assert.Equal(1, session.ReadVersion);
        Assert.Equal(1, session.Aggregate.Version);
        Assert.Equal(2, session.Aggregate.Applied);
    }

    [Fact]
    public async Task SessionSaveAsync_UncommittedEvents_AppendsThemAndClearsTheQueue()
    {
        var store = new InMemoryEventStore();
        var repository = CounterEventsRegistry.CreateRepository(store);
        var session = await repository.FetchForWritingAsync("counter-1", Ct);
        session.Aggregate.AssignId("counter-1");
        session.Aggregate.Increment();

        var result = await session.SaveAsync(Ct);

        Assert.False(result.Deduplicated);
        Assert.Equal(0, result.NextExpectedVersion);
        Assert.Empty(session.Aggregate.UncommittedEvents);
    }

    [Fact]
    public async Task SessionSaveAsync_StaleReadVersion_ThrowsConcurrencyException()
    {
        var store = new InMemoryEventStore();
        var writerRepository = CounterEventsRegistry.CreateRepository(store);
        var writer = new CounterAggregate();
        writer.AssignId("counter-1");
        writer.Increment();
        writer.Increment();
        await writerRepository.SaveAsync(writer, ExpectedVersion.NoStream, Ct); // stream at version 1 (two events)

        // The session under test gets its OWN repository (distinct MessageId): reusing
        // writerRepository here would give the stale session's single-event batch the exact same
        // deterministic EventId as writer's first event (same MessageId + stream id + batch index
        // 0), making the store treat it as a duplicate of an already-idempotent retry instead of a
        // genuinely new, guard-checked write.
        var staleRepository = CounterEventsRegistry.CreateRepository(store);
        var staleSession = await staleRepository.FetchForWritingAsync("counter-1", Ct); // ReadVersion = 1

        // A concurrent writer — a genuinely distinct command, hence its own repository/MessageId —
        // advances the stream further before the stale session gets to save.
        var concurrentRepository = CounterEventsRegistry.CreateRepository(store);
        var concurrentWriter = new CounterAggregate();
        concurrentWriter.AssignId("counter-1");
        concurrentWriter.Increment();
        await concurrentRepository.SaveAsync(concurrentWriter, ExpectedVersion.Any, Ct); // stream now at version 2

        // CounterAggregate's events carry no identity of their own (unlike a real aggregate whose
        // creation event would set Id via Apply) — the test kit's escape hatch assigns it, exactly
        // as plan 3.2 §9 (GAP-1) prescribes for this kit.
        staleSession.Aggregate.AssignId("counter-1");
        staleSession.Aggregate.Decrement();

        await Assert.ThrowsAsync<ConcurrencyException>(() => staleSession.SaveAsync(Ct).AsTask());
    }

    [Fact]
    public async Task SaveAsync_ExpectedVersion_IsForwardedVerbatimToTheStore()
    {
        var spyStore = new ExpectedVersionSpyEventStore(new InMemoryEventStore());
        var repository = CounterEventsRegistry.CreateRepository(spyStore);
        var writer = new CounterAggregate();
        writer.AssignId("counter-1");
        writer.Increment();

        var result = await repository.SaveAsync(writer, ExpectedVersion.NoStream, Ct);

        Assert.Equal(ExpectedVersion.NoStream, spyStore.LastExpectedVersion);
        Assert.False(result.Deduplicated);
        Assert.Equal(0, result.NextExpectedVersion);
    }

    [Fact]
    public async Task SaveAsync_EmptyAggregateId_ThrowsArgumentException()
    {
        var repository = CounterEventsRegistry.CreateRepository();
        var aggregate = new CounterAggregate();
        aggregate.AssignId(string.Empty);
        aggregate.Increment();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(aggregate, ExpectedVersion.NoStream, Ct).AsTask());
    }

    [Fact]
    public async Task SaveAsync_NullAggregateId_ThrowsArgumentNullException()
    {
        var repository = CounterEventsRegistry.CreateRepository();
        var aggregate = new CounterAggregate();
        aggregate.Increment();

        await Assert.ThrowsAsync<ArgumentNullException>(() => repository.SaveAsync(aggregate, ExpectedVersion.NoStream, Ct).AsTask());
    }

    [Fact]
    public async Task SaveAsync_RetryOfSameCommand_SecondSaveReturnsDeduplicatedWithoutThrowing()
    {
        var store = new InMemoryEventStore();
        var repository = CounterEventsRegistry.CreateRepository(store); // one fixed MessageId = "the command"

        var firstAttempt = new CounterAggregate();
        firstAttempt.AssignId("counter-1");
        firstAttempt.Increment();
        var first = await repository.SaveAsync(firstAttempt, ExpectedVersion.NoStream, Ct);

        // Retry: the exact same command re-executed against a fresh aggregate instance that
        // raises the identical event at the identical batch position (index 0) — the caller
        // retries with the stream's ORIGINAL expected version because it cannot tell whether the
        // first attempt committed.
        var retryAttempt = new CounterAggregate();
        retryAttempt.AssignId("counter-1");
        retryAttempt.Increment();
        var retry = await repository.SaveAsync(retryAttempt, ExpectedVersion.NoStream, Ct);

        Assert.False(first.Deduplicated);
        Assert.True(retry.Deduplicated);
        Assert.Empty(retryAttempt.UncommittedEvents);
        var stored = await ToListAsync(store.ReadStreamAsync("counter-1", ct: Ct));
        Assert.Single(stored);
    }

    [Fact]
    public async Task SaveAsync_InjectedMetadataFactory_StampsItsMetadataOnEveryAppendedEvent()
    {
        var store = new InMemoryEventStore();
        var metadataFactory = CounterEventsRegistry.FixedMetadataFactory();
        var repository = CounterEventsRegistry.CreateRepository(store, metadataFactory);
        var writer = new CounterAggregate();
        writer.AssignId("counter-1");
        writer.Increment();

        await repository.SaveAsync(writer, ExpectedVersion.NoStream, Ct);

        var expectedMetadata = metadataFactory();
        var stored = await ToListAsync(store.ReadStreamAsync("counter-1", ct: Ct));
        Assert.Equal(expectedMetadata.MessageId, stored[0].Metadata.MessageId);
        Assert.Equal(expectedMetadata.CorrelationId, stored[0].Metadata.CorrelationId);
        Assert.Equal(expectedMetadata.CausationId, stored[0].Metadata.CausationId);
    }

    [Fact]
    public async Task SaveAsync_MultiEventBatch_UsesDistinctEventIdPerIndexAndAppendsAtomically()
    {
        var store = new InMemoryEventStore();
        var repository = CounterEventsRegistry.CreateRepository(store);
        var writer = new CounterAggregate();
        writer.AssignId("counter-1");
        writer.Increment();
        writer.Increment();
        writer.Decrement();

        var result = await repository.SaveAsync(writer, ExpectedVersion.NoStream, Ct);

        Assert.Equal(2, result.NextExpectedVersion);
        var stored = await ToListAsync(store.ReadStreamAsync("counter-1", ct: Ct));
        Assert.Equal(3, stored.Count);
        Assert.Equal(stored.Select(e => e.EventId).Distinct().Count(), stored.Count);
    }

    [Fact]
    public async Task SaveAsync_EmptyUncommittedEvents_IsNoOpAndAppendsNothing()
    {
        var store = new InMemoryEventStore();
        var repository = CounterEventsRegistry.CreateRepository(store);
        var writer = new CounterAggregate();
        writer.AssignId("counter-1");
        writer.Increment();
        await repository.SaveAsync(writer, ExpectedVersion.NoStream, Ct); // one event persisted; queue cleared

        var result = await repository.SaveAsync(writer, ExpectedVersion.Any, Ct); // nothing new to append

        Assert.False(result.Deduplicated);
        Assert.Equal(0, result.NextExpectedVersion);
        var stored = await ToListAsync(store.ReadStreamAsync("counter-1", ct: Ct));
        Assert.Single(stored);
    }

    /// <summary>
    /// Known-limitation test (GAP-2, plan 3.2 §2.3/§9): a second, genuinely new write to a stream
    /// that currently holds EXACTLY one event (<c>ReadVersion == 0</c>, coinciding with
    /// <see cref="ExpectedVersion.EmptyStream"/>) is semantically valid — the caller's view of the
    /// stream IS current — yet this in-memory backend throws <see cref="ConcurrencyException"/>
    /// instead of succeeding, because it collapses <c>EmptyStream</c> onto "stream must not exist"
    /// and cannot distinguish "empty and existing" from "never created". Cross-backend
    /// ratification is deferred to tasks 2.2/7.2 (see <c>InMemoryEventStore</c> remarks) — this
    /// test pins today's documented in-memory contract so a silent behavior change would fail it.
    /// </summary>
    [Fact]
    public async Task SaveAsync_SecondWriteAfterSingleEventLoad_ThrowsConcurrencyException_Gap2KnownLimitation()
    {
        var store = new InMemoryEventStore();
        var repository = CounterEventsRegistry.CreateRepository(store);
        var firstWriter = new CounterAggregate();
        firstWriter.AssignId("counter-1");
        firstWriter.Increment();
        await repository.SaveAsync(firstWriter, ExpectedVersion.NoStream, Ct); // stream now holds exactly one event, version 0

        var secondRepository = CounterEventsRegistry.CreateRepository(store); // a distinct command → distinct MessageId
        var session = await secondRepository.FetchForWritingAsync("counter-1", Ct);
        Assert.Equal(0, session.ReadVersion); // exactly the 0->1 boundary this test documents
        session.Aggregate.AssignId("counter-1");
        session.Aggregate.Increment();

        await Assert.ThrowsAsync<ConcurrencyException>(() => session.SaveAsync(Ct).AsTask());
    }

    /// <summary>Records the <c>expectedVersion</c> passed to the last <c>AppendAsync</c> call, delegating everything else.</summary>
    private sealed class ExpectedVersionSpyEventStore(IEventStore inner) : IEventStore
    {
        public long? LastExpectedVersion { get; private set; }

        public ValueTask<AppendResult> AppendAsync(
            string streamId, long expectedVersion, IReadOnlyList<EventData> events, CancellationToken ct = default)
        {
            LastExpectedVersion = expectedVersion;
            return inner.AppendAsync(streamId, expectedVersion, events, ct);
        }

        public IAsyncEnumerable<StoredEvent> ReadStreamAsync(
            string streamId,
            long fromVersion = 0,
            long? toVersion = null,
            Direction direction = Direction.Forwards,
            CancellationToken ct = default)
            => inner.ReadStreamAsync(streamId, fromVersion, toVersion, direction, ct);

        public IAsyncEnumerable<StoredEvent> ReadAllAsync(
            GlobalPosition from,
            GlobalPosition? upTo = null,
            int? maxCount = null,
            Direction direction = Direction.Forwards,
            CancellationToken ct = default)
            => inner.ReadAllAsync(from, upTo, maxCount, direction, ct);
    }
}
