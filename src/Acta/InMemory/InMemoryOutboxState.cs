using System.Collections.Immutable;

using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// The shared, atomically-published committed state behind the AK-1 single-commit seam
/// (ADR-002, FR-14): every domain event appended and every integration event enlisted through an
/// <see cref="InMemoryEventAppendTransaction"/> becomes visible here in one all-or-nothing swap
/// when <see cref="InMemoryEventAppendTransaction.CommitAsync"/> is called — never partially.
/// <para>
/// <b>Concurrency design (lock+swap, mirrors <see cref="InMemoryEventStore"/>):</b> the whole
/// committed state is one immutable <see cref="State"/> object referenced by <c>_state</c>.
/// <see cref="Commit"/> takes the exclusive write lock, re-validates every buffered append's
/// optimistic-concurrency guard against the freshest committed snapshot, builds a brand-new
/// <see cref="State"/> with the appends and outbox entries folded in, and publishes it with a
/// single <see cref="Volatile.Write{T}"/>. Readers (<see cref="ReadStream"/>,
/// <see cref="CommittedOutbox"/>, <see cref="CommittedEventCount"/>) take no lock at all: each
/// does one <see cref="Volatile.Read{T}"/> and reads that immutable snapshot to completion.
/// </para>
/// <para>
/// <b>D-8.3-A:</b> deliberately keeps its own state instead of wrapping
/// <see cref="InMemoryEventStore"/>: that store's write lock is private and scoped to a single
/// <c>AppendAsync</c> call, so it cannot fold outbox entries into the same atomic commit that
/// AK-1 requires. Appends made through this seam are therefore <b>not</b> visible through an
/// <see cref="InMemoryEventStore"/> instance, and vice versa — the two are intentionally
/// independent, in-memory stores.
/// </para>
/// <para>
/// <see cref="State.ByStream"/> holds <see cref="ImmutableList{T}"/> values, never
/// <see cref="ImmutableArray{T}"/>: the list is a persistent balanced tree with O(log n)
/// <c>AddRange</c>, whereas <see cref="ImmutableArray{T}"/>'s <c>Add</c> copies the entire backing
/// array on every call — do not swap this for <see cref="ImmutableArray{T}"/>.
/// </para>
/// <para>
/// Unlike <see cref="InMemoryEventStore"/>, this seam performs no append-time deduplication — a
/// deliberate simplification out of scope for AK-1, not to be confused with command idempotency
/// (task 8.5): <see cref="AppendResult.Deduplicated"/> is always <see langword="false"/> here.
/// </para>
/// </summary>
public sealed class InMemoryOutboxState
{
    private readonly Lock _writeLock = new();
    private State _state = State.Empty;

    /// <summary>Every integration event committed so far, in commit order.</summary>
    public IReadOnlyList<CollectedIntegrationEvent> CommittedOutbox => Volatile.Read(ref _state).Outbox;

    /// <summary>The total number of domain events committed so far, across every stream.</summary>
    public int CommittedEventCount => Volatile.Read(ref _state).ByStream.Values.Sum(events => events.Count);

    /// <summary>The <see cref="GlobalPosition"/> value to assign to the next committed event.</summary>
    internal long NextGlobalPosition => Volatile.Read(ref _state).NextGlobalPosition;

    /// <summary>
    /// The committed events of <paramref name="streamId"/>, in version order, or empty if the
    /// stream does not exist (or nothing has been committed to it yet).
    /// </summary>
    /// <param name="streamId">Identifier of the stream to read.</param>
    public IReadOnlyList<StoredEvent> ReadStream(string streamId)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);

        var state = Volatile.Read(ref _state);
        return state.ByStream.TryGetValue(streamId, out var events) ? events : [];
    }

    /// <summary>
    /// Re-validates every pending append's optimistic-concurrency guard against the freshest
    /// committed snapshot and, if all pass, atomically publishes the appended domain events
    /// together with the pending outbox entries in a single swap (AK-1: all-or-nothing).
    /// </summary>
    /// <param name="pendingAppends">The transaction's buffered, not-yet-committed appends, in call order.</param>
    /// <param name="pendingOutbox">The transaction's buffered, not-yet-committed outbox entries.</param>
    /// <param name="timeProvider">Clock used to stamp <see cref="StoredEvent.Timestamp"/>.</param>
    /// <exception cref="ConcurrencyException">
    /// A pending append's guard was violated against the state at commit time.
    /// </exception>
    internal void Commit(
        IReadOnlyList<InMemoryEventAppendTransaction.PendingAppend> pendingAppends,
        IReadOnlyList<CollectedIntegrationEvent> pendingOutbox,
        TimeProvider timeProvider)
    {
        lock (_writeLock)
        {
            var state = Volatile.Read(ref _state);
            var byStream = state.ByStream;
            var nextGlobalPosition = state.NextGlobalPosition;
            var timestamp = timeProvider.GetUtcNow();

            foreach (var pending in pendingAppends)
            {
                byStream.TryGetValue(pending.StreamId, out var streamEvents);
                var currentLastVersion = streamEvents?[^1].Version ?? -1L;

                if (pending.Events.Count == 0)
                {
                    continue;
                }

                ValidateExpectedVersion(pending.StreamId, pending.ExpectedVersion, currentLastVersion);

                var appended = new StoredEvent[pending.Events.Count];
                for (var i = 0; i < pending.Events.Count; i++)
                {
                    var eventData = pending.Events[i];
                    appended[i] = new StoredEvent(
                        eventData.EventId,
                        pending.StreamId,
                        currentLastVersion + 1 + i,
                        new GlobalPosition(nextGlobalPosition + i),
                        eventData.EventType,
                        eventData.SchemaVersion,
                        eventData.Payload,
                        eventData.Metadata,
                        timestamp);
                }

                streamEvents = (streamEvents ?? ImmutableList<StoredEvent>.Empty).AddRange(appended);
                byStream = byStream.SetItem(pending.StreamId, streamEvents);
                nextGlobalPosition += appended.Length;
            }

            var outbox = state.Outbox.AddRange(pendingOutbox);
            Volatile.Write(ref _state, new State(byStream, outbox, nextGlobalPosition));
        }
    }

    /// <summary>
    /// Guard matrix shared with <see cref="InMemoryEventStore"/> (ADR-003, 03-contracts.md §1):
    /// <c>Any</c> never guards; <c>NoStream</c> requires the stream to not yet exist;
    /// <c>StreamExists</c> requires it to exist; an exact version <c>&gt;= 1</c> requires an exact
    /// match. <c>EmptyStream</c> collapses onto <c>NoStream</c> here — the same documented
    /// in-memory default as <see cref="InMemoryEventStore"/>.
    /// </summary>
    internal static void ValidateExpectedVersion(string streamId, long expectedVersion, long currentLastVersion)
    {
        var streamExists = currentLastVersion >= 0;

        var guardSatisfied = expectedVersion switch
        {
            ExpectedVersion.Any => true,
            ExpectedVersion.NoStream => !streamExists,
            ExpectedVersion.StreamExists => streamExists,
            ExpectedVersion.EmptyStream => !streamExists,
            _ => currentLastVersion == expectedVersion,
        };

        if (!guardSatisfied)
        {
            throw new ConcurrencyException(streamId, expectedVersion, currentLastVersion);
        }
    }

    /// <summary>
    /// The whole committed state as one immutable snapshot (lock+swap design — see the class remarks).
    /// </summary>
    /// <param name="ByStream">Per-stream committed event lists, each ordered by <see cref="StoredEvent.Version"/> and gapless.</param>
    /// <param name="Outbox">Every committed integration event, in commit order.</param>
    /// <param name="NextGlobalPosition">
    /// The <see cref="GlobalPosition"/> value to assign to the next newly-committed event
    /// (1-based; starts at 1 on an empty state).
    /// </param>
    private sealed record State(
        ImmutableDictionary<string, ImmutableList<StoredEvent>> ByStream,
        ImmutableList<CollectedIntegrationEvent> Outbox,
        long NextGlobalPosition)
    {
        public static readonly State Empty = new(
            ImmutableDictionary<string, ImmutableList<StoredEvent>>.Empty,
            ImmutableList<CollectedIntegrationEvent>.Empty,
            NextGlobalPosition: 1);
    }
}
