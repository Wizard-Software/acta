using Acta.Abstractions;
using Acta.Projections.Inline;

namespace Acta.Projections.Daemon;

/// <summary>
/// The minimal registration of one asynchronous projection led by <see cref="ProjectionDaemon"/>
/// (task 5.2, decision D5): the checkpoint key (<see cref="Name"/>), the event-type filter pushed
/// down to <c>ISubscriptionSource.ReadBatchAsync</c> (<see cref="EventTypes"/>), and the
/// single-projection dispatcher (<see cref="Runner"/> — an <see cref="InlineProjectionRunner"/>
/// reused for deserialization, ordered <c>ApplyAsync</c>, and the per-projection idempotency
/// watermark).
/// <para>
/// This is deliberately NOT the full <c>ProjectionDefinition(Name, EventTypes, ErrorPolicy, Mode)</c>
/// of MODULE-INTERFACES §Grupa 5: <c>Mode</c> still has no executor (Tier 1/2 only ever runs Async
/// registrations here), so introducing it without a reader would violate CONSTITUTION §2 (ASK
/// FIRST). <see cref="ErrorPolicy"/> (task 5.4) attached additively, exactly as foreshadowed by the
/// original 5.2 design (mitigation R-D) — the retry/dead-letter/pause policy engine that consumes it
/// lives in <see cref="ProjectionDaemon"/>.
/// </para>
/// <para>
/// <b>Lead state.</b> The mutable fields below (<see cref="IsHalted"/>, <see cref="IsInCatchUp"/>,
/// <see cref="CachedCheckpoint"/>) are the daemon's per-projection bookkeeping, mutated only from the
/// daemon's single <c>ExecuteAsync</c> task (no cross-thread access, single-process — ADR-014), and
/// visible to the test project via <c>InternalsVisibleTo</c>.
/// </para>
/// </summary>
public sealed class AsyncProjectionRegistration
{
    /// <summary>
    /// Creates a registration binding a projection name, its event-type filter, its
    /// single-projection dispatcher, and its error policy.
    /// </summary>
    /// <param name="name">The projection name — the checkpoint key (<c>Load/SaveAsync</c>).</param>
    /// <param name="eventTypes">The event-type filter pushed down to <c>ReadBatchAsync</c>.</param>
    /// <param name="runner">The dispatcher for this one projection (built over a single <c>IProjection</c>).</param>
    /// <param name="errorPolicy">
    /// The retry/dead-letter/pause policy for this projection's apply failures (task 5.4);
    /// <see langword="null"/> resolves to <c>new ProjectionErrorPolicy()</c> (dead-letter-and-skip,
    /// three retries) — an additive, trailing optional parameter so existing call sites keep
    /// compiling unchanged.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="eventTypes"/> or <paramref name="runner"/> is null.</exception>
    public AsyncProjectionRegistration(
        string name,
        IReadOnlySet<string> eventTypes,
        InlineProjectionRunner runner,
        ProjectionErrorPolicy? errorPolicy = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(eventTypes);
        ArgumentNullException.ThrowIfNull(runner);

        Name = name;
        EventTypes = eventTypes;
        Runner = runner;
        ErrorPolicy = errorPolicy ?? new ProjectionErrorPolicy();
    }

    /// <summary>The projection name — the checkpoint key for <c>ICheckpointSink.Load/SaveAsync</c>.</summary>
    public string Name { get; }

    /// <summary>The event types this projection consumes — pushed down to <c>ReadBatchAsync</c> (ADR-015).</summary>
    public IReadOnlySet<string> EventTypes { get; }

    /// <summary>The single-projection dispatcher (deserialize + ordered <c>ApplyAsync</c> + idempotency watermark).</summary>
    public InlineProjectionRunner Runner { get; }

    /// <summary>
    /// The retry/dead-letter/pause policy this projection's apply failures are governed by (task
    /// 5.4). Defaults to <c>new ProjectionErrorPolicy()</c> (dead-letter-and-skip, three retries)
    /// when not supplied at construction.
    /// </summary>
    public ProjectionErrorPolicy ErrorPolicy { get; }

    /// <summary>
    /// <see langword="true"/> once this projection has been halted — either by the error policy's
    /// <see cref="ErrorAction.Pause"/> action after retries were exhausted (task 5.4), or, before
    /// task 5.4 existed, by the 5.2 baseline: the daemon stops leading a halted projection while the
    /// daemon and every other projection continue (ADR-005 — one poisoned event never stops the
    /// daemon).
    /// </summary>
    internal bool IsHalted { get; private set; }

    /// <summary>
    /// <see langword="true"/> while this projection has a matching backlog past the backpressure
    /// threshold — the daemon drains it without the inter-tick polling delay. Cleared as soon as the
    /// matching backlog is exhausted (an empty or partial batch), which is what keeps a type-selective
    /// projection from wedging the daemon in a zero-delay busy-spin (correction V-2).
    /// </summary>
    internal bool IsInCatchUp { get; private set; }

    /// <summary>
    /// The last checkpoint the daemon cached for this projection, or <see langword="null"/> when it
    /// must be (re)loaded from the sink — at first lead, or after a fence invalidated the cache.
    /// </summary>
    internal GlobalPosition? CachedCheckpoint { get; private set; }

    /// <summary>Marks this projection faulted so the daemon stops leading it (baseline halt, 5.2).</summary>
    internal void Halt() => IsHalted = true;

    /// <summary>Enters catch-up mode: the daemon drains this projection without the polling delay.</summary>
    internal void EnterCatchUp() => IsInCatchUp = true;

    /// <summary>Leaves catch-up mode: the matching backlog is drained, the polling delay resumes.</summary>
    internal void ExitCatchUp() => IsInCatchUp = false;

    /// <summary>Caches <paramref name="position"/> as the last known checkpoint (trusted next tick).</summary>
    internal void CacheCheckpoint(GlobalPosition position) => CachedCheckpoint = position;

    /// <summary>
    /// Drops leadership after a <see cref="CheckpointFencedException"/>: invalidates the cached
    /// checkpoint so the next tick reloads it from the sink (zombie-guard, Postgres readiness — the
    /// in-memory sink never fences, D8). Single-process has no lock to release (no-op there).
    /// </summary>
    internal void DropLeadership() => CachedCheckpoint = null;
}
