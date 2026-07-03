using System.Collections.Immutable;
using System.Runtime.CompilerServices;

using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local implementation of <see cref="IEventStore"/> — the Tier 1 backend
/// used by <c>AddActa()</c> and by tests.
/// <para>
/// <b>Concurrency design (lock+swap, NFR-2):</b> the whole store state is one immutable
/// <see cref="State"/> object referenced by <see cref="_state"/>. Every <see cref="AppendAsync"/>
/// call takes an exclusive <see cref="_writeLock"/>, reads the current <see cref="State"/>,
/// validates the concurrency guard and dedup set against it, builds a brand-new
/// <see cref="State"/> with the appended events folded in, and publishes it with a single
/// <see cref="Volatile.Write{T}"/>. Readers (<see cref="ReadStreamAsync"/>,
/// <see cref="ReadAllAsync"/>) take no lock at all: each read does exactly one
/// <see cref="Volatile.Read{T}"/> at the start of enumeration and then iterates that snapshot to
/// completion. Because <see cref="State"/> and every collection it holds are immutable, a
/// concurrent append can never mutate a snapshot a reader is iterating — there is no torn read,
/// and the batch either is or is not visible in its entirety (all-or-nothing), never partially.
/// </para>
/// <para>
/// <see cref="State.Global"/> is deliberately an <see cref="ImmutableList{T}"/>, not an
/// <see cref="ImmutableArray{T}"/>: <see cref="ImmutableList{T}"/> is a persistent balanced tree
/// with O(log n) <c>Add</c>/<c>AddRange</c>, whereas <see cref="ImmutableArray{T}"/>'s
/// <c>Add</c> copies the entire backing array on every call (O(n) per append, O(n²) for a long
/// append history) — do not swap this for <see cref="ImmutableArray{T}"/>.
/// </para>
/// <para>
/// <b>Version / GlobalPosition assignment:</b> <see cref="StoredEvent.Version"/> is 0-based,
/// monotonic, and gapless per stream — a stream's <c>N</c>th event has version <c>N-1</c>.
/// <see cref="Acta.Abstractions.GlobalPosition"/> is 1-based across the whole store — the very
/// first event ever appended gets position 1; <see cref="GlobalPosition.Start"/> (0) is reserved
/// as the "nothing consumed yet" sentinel and is never assigned to an event. This backend never
/// produces gaps in <see cref="Acta.Abstractions.GlobalPosition"/> (ADR-001 permits gaps as
/// backend-specific behavior; they are not a requirement).
/// </para>
/// <para>
/// <b>ExpectedVersion guard (03-contracts.md §1, ADR-003):</b> <c>Any</c> never guards;
/// <c>NoStream</c> requires the stream to not yet exist; <c>StreamExists</c> requires it to
/// exist; an exact version <c>&gt;= 1</c> requires the stream's last version to match exactly.
/// <c>EmptyStream</c> (0) is contractually "the stream exists and is empty" — but this in-memory
/// backend has no separate notion of stream existence besides "has at least one event", so a
/// 0-event stream cannot be distinguished from a never-created one. As a documented in-memory
/// default (GAP-2, pending ratification against the Postgres backend by the 2.2 contract test
/// suite), <c>EmptyStream</c> is therefore guarded exactly like <c>NoStream</c> here: the first
/// append to a brand-new stream succeeds under either sentinel.
/// </para>
/// <para>
/// <b>Deduplication (03-contracts.md §1, ADR-003):</b> every appended event is keyed by
/// <c>(streamId, EventId)</c> in an unconditional, guard-independent dedup set. A full-batch
/// replay — every key in the incoming batch already present — is recognized <i>before</i> the
/// concurrency guard is evaluated and returns an idempotent success
/// (<see cref="AppendResult.Deduplicated"/> = <see langword="true"/>, nothing appended), even if
/// the guard would otherwise have failed: a retry of an already-applied command must never throw
/// merely because the stream has since moved on. As a documented in-memory default for a
/// <i>partially</i>-overlapping batch (GAP-1, explicitly <b>not yet ratified</b> as binding
/// cross-backend behavior — see plan 2.1 §9 Q2, to be settled by the 2.2 contract tests), this
/// backend treats <i>any</i> already-seen key in the batch as sufficient to treat the whole batch
/// as a duplicate: none of the batch's events are appended, including any genuinely new ones
/// mixed into it. This differs from a naïve per-row <c>ON CONFLICT DO NOTHING</c> semantics (which
/// would append the new rows and skip only the duplicates) — callers must not rely on this exact
/// shape until 2.2 ratifies it for both backends.
/// </para>
/// <para>
/// <b>Read guard (ADR-015):</b> <see cref="ReadAllAsync"/> never applies an artificial
/// visibility-lag "cofka" the way the Postgres backend must. A visibility lag exists there only
/// because a database can reserve a sequence value for a transaction that has not committed yet,
/// making a lower <see cref="Acta.Abstractions.GlobalPosition"/> briefly invisible after a higher
/// one. Here, <see cref="Acta.Abstractions.GlobalPosition"/> assignment and the swap that
/// publishes it happen under the very same <see cref="_writeLock"/> that serializes all appends —
/// there is no window in which an in-flight append could later produce a position lower than one
/// already visible. Consequently, the safe high-water mark for this backend is simply every event
/// present in the current snapshot, and the shared ADR-015 contract test passes trivially: this
/// backend can never violate the "no event above the safe high-water mark" invariant.
/// </para>
/// <para>
/// <b>Multi-pod behavior class (ADR-014, D14): single-process ONLY.</b> All state lives in this
/// instance's process-local memory (<see cref="_state"/>); nothing is shared or coordinated
/// across pods. Running two or more pods against logically "the same" <see cref="InMemoryEventStore"/>
/// (they cannot actually share one — each process gets its own) silently produces divergent,
/// inconsistent event histories with no error. This type intentionally has no built-in multi-pod
/// detection or throw site for <see cref="SingleProcessOnlyException"/> — see that type's
/// documentation for why detection is a forward dependency of the host/DI layer. Use the Postgres
/// backend (<c>AddActaPostgres</c>) for any topology with more than one pod (ADR-014).
/// </para>
/// </summary>
/// <param name="timeProvider">
/// Clock used to stamp <see cref="StoredEvent.Timestamp"/> on every appended event, injectable
/// for deterministic tests. <see langword="null"/> (the default) resolves to
/// <see cref="TimeProvider.System"/>.
/// </param>
public sealed class InMemoryEventStore(TimeProvider? timeProvider = null) : IEventStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _writeLock = new();
    private State _state = State.Empty;

    /// <inheritdoc/>
    public ValueTask<AppendResult> AppendAsync(
        string streamId,
        long expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();

        lock (_writeLock)
        {
            var state = Volatile.Read(ref _state);
            state.ByStream.TryGetValue(streamId, out var streamEvents);
            var currentLastVersion = streamEvents?[^1].Version ?? -1L;
            var currentHeadPosition = streamEvents?[^1].GlobalPosition ?? GlobalPosition.Start;

            // Open Question #3 (plan 2.1 §9 Q3): an empty batch is a safe, idempotent no-op — it
            // touches neither the concurrency guard nor the dedup set, and simply reports the
            // stream's current head unchanged.
            if (events.Count == 0)
            {
                return ValueTask.FromResult(new AppendResult(currentLastVersion, currentHeadPosition, Deduplicated: false));
            }

            // ADR-003 / GAP-1: a full-batch replay is recognized BEFORE the concurrency guard —
            // see the class remarks for the exact in-memory default and its cross-backend caveat.
            if (HasAnyDuplicateKey(state.Dedup, streamId, events))
            {
                return ValueTask.FromResult(new AppendResult(currentLastVersion, currentHeadPosition, Deduplicated: true));
            }

            ValidateExpectedVersion(streamId, expectedVersion, currentLastVersion);

            var timestamp = _timeProvider.GetUtcNow();
            var appended = new StoredEvent[events.Count];
            for (var i = 0; i < events.Count; i++)
            {
                var eventData = events[i];
                appended[i] = new StoredEvent(
                    eventData.EventId,
                    streamId,
                    currentLastVersion + 1 + i,
                    new GlobalPosition(state.NextGlobalPosition + i),
                    eventData.EventType,
                    eventData.SchemaVersion,
                    eventData.Payload,
                    eventData.Metadata,
                    timestamp);
            }

            var updatedGlobal = state.Global.AddRange(appended);
            var updatedStreamEvents = (streamEvents ?? ImmutableList<StoredEvent>.Empty).AddRange(appended);
            var updatedByStream = state.ByStream.SetItem(streamId, updatedStreamEvents);
            var updatedDedup = state.Dedup.Union(appended.Select(e => new StreamEventKey(streamId, e.EventId)));
            var newState = new State(updatedGlobal, updatedByStream, updatedDedup, state.NextGlobalPosition + appended.Length);

            Volatile.Write(ref _state, newState);

            var last = appended[^1];
            return ValueTask.FromResult(new AppendResult(last.Version, last.GlobalPosition, Deduplicated: false));
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StoredEvent> ReadStreamAsync(
        string streamId,
        long fromVersion = 0,
        long? toVersion = null,
        Direction direction = Direction.Forwards,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);

        return ReadStreamCoreAsync(streamId, fromVersion, toVersion, direction, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StoredEvent> ReadAllAsync(
        GlobalPosition from,
        GlobalPosition? upTo = null,
        int? maxCount = null,
        Direction direction = Direction.Forwards,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // No real asynchronous work happens below — this backend is fully synchronous
        // process-local memory (ADR-014) — but a genuine async-iterator method needs one await
        // to avoid CS1998; it also gives future backends a drop-in-compatible signature.
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();

        var state = Volatile.Read(ref _state);
        var filtered = state.Global.Where(e => e.GlobalPosition > from && (upTo is null || e.GlobalPosition <= upTo.Value));
        var ordered = OrderForRead(filtered, direction);
        var limited = maxCount is { } limit ? ordered.Take(limit) : ordered;

        foreach (var storedEvent in limited)
        {
            ct.ThrowIfCancellationRequested();
            yield return storedEvent;
        }
    }

    private async IAsyncEnumerable<StoredEvent> ReadStreamCoreAsync(
        string streamId,
        long fromVersion,
        long? toVersion,
        Direction direction,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask; // See the remark in ReadAllAsync — silences CS1998 by design.
        ct.ThrowIfCancellationRequested();

        var state = Volatile.Read(ref _state);
        if (!state.ByStream.TryGetValue(streamId, out var streamEvents))
        {
            yield break;
        }

        var filtered = streamEvents.Where(e => e.Version >= fromVersion && (toVersion is null || e.Version <= toVersion.Value));

        foreach (var storedEvent in OrderForRead(filtered, direction))
        {
            ct.ThrowIfCancellationRequested();
            yield return storedEvent;
        }
    }

    private static IEnumerable<StoredEvent> OrderForRead(IEnumerable<StoredEvent> events, Direction direction)
        => direction == Direction.Backwards ? events.Reverse() : events;

    private static bool HasAnyDuplicateKey(ImmutableHashSet<StreamEventKey> dedup, string streamId, IReadOnlyList<EventData> events)
    {
        foreach (var eventData in events)
        {
            if (dedup.Contains(new StreamEventKey(streamId, eventData.EventId)))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateExpectedVersion(string streamId, long expectedVersion, long currentLastVersion)
    {
        var streamExists = currentLastVersion >= 0;

        var guardSatisfied = expectedVersion switch
        {
            ExpectedVersion.Any => true,
            ExpectedVersion.NoStream => !streamExists,
            ExpectedVersion.StreamExists => streamExists,

            // GAP-2 (documented in-memory default — see class remarks): collapses onto NoStream.
            ExpectedVersion.EmptyStream => !streamExists,

            _ => currentLastVersion == expectedVersion,
        };

        if (!guardSatisfied)
        {
            throw new ConcurrencyException(streamId, expectedVersion, currentLastVersion);
        }
    }

    /// <summary>
    /// The whole store as one immutable snapshot (lock+swap design — see the class remarks).
    /// </summary>
    /// <param name="Global">Every event ever appended, ordered by <see cref="Acta.Abstractions.GlobalPosition"/>.</param>
    /// <param name="ByStream">Per-stream event lists, each ordered by <see cref="StoredEvent.Version"/> and gapless.</param>
    /// <param name="Dedup">The unconditional <c>(streamId, EventId)</c> deduplication set.</param>
    /// <param name="NextGlobalPosition">
    /// The <see cref="Acta.Abstractions.GlobalPosition"/> value to assign to the next
    /// newly-appended event (1-based; starts at 1 on an empty store).
    /// </param>
    private sealed record State(
        ImmutableList<StoredEvent> Global,
        ImmutableDictionary<string, ImmutableList<StoredEvent>> ByStream,
        ImmutableHashSet<StreamEventKey> Dedup,
        long NextGlobalPosition)
    {
        public static readonly State Empty = new(
            ImmutableList<StoredEvent>.Empty,
            ImmutableDictionary<string, ImmutableList<StoredEvent>>.Empty,
            ImmutableHashSet<StreamEventKey>.Empty,
            NextGlobalPosition: 1);
    }

    /// <summary>The unconditional deduplication key: an event id scoped to its stream (ADR-003).</summary>
    private readonly record struct StreamEventKey(string StreamId, Guid EventId);
}
