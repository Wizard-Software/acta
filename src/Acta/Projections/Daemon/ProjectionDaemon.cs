using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Acta.Abstractions;
using Acta.Configuration;

namespace Acta.Projections.Daemon;

/// <summary>
/// The asynchronous projection daemon (task 5.2, 05-implementation §3) — a
/// <see cref="BackgroundService"/> that drives every registered async projection over the catch-up
/// subscription. Single-process, single-tenant (ADR-014): there is no leader election, so ALL
/// registered projections are led; multi-pod election/fencing (7.5/7.6) and multi-tenant (Feature
/// 10) are out of scope.
/// <para>
/// <b>One tick</b> (<see cref="RunTickAsync"/>): read the safe high-water mark once and share it
/// across projections (P×T → 1), then, per non-halted projection, drain matching events in batches
/// of <see cref="ProjectionDaemonOptions.BatchSize"/> from its checkpoint — dispatch its events
/// through the reused <c>InlineProjectionRunner</c> (ordered, idempotent apply, one event at a time
/// under the error policy) and advance the checkpoint once per batch via a fenced
/// <c>ICheckpointSink.SaveAsync</c>.
/// </para>
/// <para>
/// <b>Backpressure / catch-up.</b> A projection with a matching backlog past
/// <see cref="ProjectionDaemonOptions.PendingEventsThreshold"/> stays in catch-up mode — the daemon
/// loops ticks without the <see cref="ProjectionDaemonOptions.PollingInterval"/> delay until the
/// backlog drains. The catch-up flag is cleared the moment a projection's matching backlog is
/// exhausted (an empty or partial batch, or reaching the global head), which prevents a
/// type-selective projection — whose checkpoint never reaches the global head because trailing
/// events are non-matching — from wedging the daemon in a zero-delay busy-spin (correction V-2).
/// </para>
/// <para>
/// <b>Error policy (task 5.4 — retry → dead-letter | pause).</b> An event whose apply throws is
/// retried, in place, up to <see cref="ProjectionErrorPolicy.MaxRetries"/> times — safe because of
/// the reused runner's mark-after-apply idempotency watermark, so a retry only ever re-applies the
/// still-unwatermarked poisoned event. Once retries are exhausted, the event is recorded in the
/// shared <see cref="DeadLetterBuffer"/> and the projection's
/// <see cref="ProjectionErrorPolicy.OnApplyError"/> decides what happens next:
/// <see cref="ErrorAction.DeadLetterAndSkip"/> advances the checkpoint past the poisoned event so the
/// projection and the daemon keep running; <see cref="ErrorAction.Pause"/> halts THAT projection only
/// — the checkpoint stops right before the poisoned event, so a rebuild or a manual resume retries it
/// — while the daemon and every other projection continue either way (ADR-005 — one poisoned event
/// never stops the daemon).
/// <b>Caveat:</b> a dead-letter entry's <c>Error</c> field, and the accompanying
/// <see cref="LogLevel.Error"/> log line, carry the failing exception's type and message only — never
/// the payload/event/metadata (ADR-008/017) — but neither can scrub a caller-authored exception
/// message: a host projection whose own <c>ApplyAsync</c> builds an exception message from record
/// data (e.g. <c>$"failed for {order.Email}"</c>) surfaces that text into both verbatim. Hosts must
/// keep PII out of their exception messages (mirrors <c>InlineProjectionRunner</c> and
/// <see cref="DeadLetterBuffer"/>'s own caveat).
/// </para>
/// <para>
/// <b>Graceful stop.</b> Cancellation (host stop) ends the loop after the current batch — a batch
/// cancelled in flight does not save its checkpoint, so the events replay safely on restart
/// (idempotent, at-least-once). A <see cref="CheckpointFencedException"/> makes the daemon STOP
/// leading that projection: this instance's owner token is permanently stale, so re-leading it would
/// re-apply events and re-fence on every tick (Postgres readiness; the in-memory sink never fences, D8).
/// </para>
/// <para>
/// <b>Gap policy (task 5.3 — <see cref="GapGuard"/>, ADR-001 R3).</b> A projection's
/// checkpoint sitting below the safe HWM after an empty matching-event batch either faces a
/// non-matching tail (correction V-2 — no gap, current behavior unchanged) or a true hole in
/// <see cref="GlobalPosition"/>. <see cref="GapGuard.Evaluate"/> tells the two apart via a cheap
/// raw-stream peek and decides whether a true gap should be waited on (a still-in-flight write may
/// yet fill it) or skipped now — advancing the checkpoint past it through the same fenced CAS save
/// used by the batch-apply path, and recording the skip (<c>acta.projection.gaps_skipped</c> plus a
/// diagnostic warning) via <see cref="GapGuard.RecordSkip"/>. This branch is semantically disjoint
/// from the apply-error policy above: it only fires when there are no matching events left to apply.
/// </para>
/// </summary>
public sealed class ProjectionDaemon : BackgroundService
{
    private readonly ISubscriptionSource _source;
    private readonly ICheckpointSink _checkpoints;
    private readonly HwmPoller _hwmPoller;
    private readonly IReadOnlyList<AsyncProjectionRegistration> _projections;
    private readonly ProjectionDaemonOptions _daemon;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProjectionDaemon> _logger;
    private readonly DeadLetterBuffer _deadLetters;
    private readonly GapGuard _gapGuard;

    // One owner token per daemon instance (leadership fencing, D7). The in-memory sink validates it
    // non-empty but never compares it (Tier-1 has no split-brain); Postgres (7.5/7.6) enforces it.
    private readonly string _ownerToken = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Constructs the daemon from its collaborators. <paramref name="timeProvider"/> is optional and
    /// falls back to <see cref="TimeProvider.System"/> — the composition root does not register a
    /// <see cref="TimeProvider"/> by default, so a required parameter would fault DI resolution
    /// (correction V-1; mirrors <c>InMemoryEventStore</c>).
    /// </summary>
    /// <param name="source">The catch-up subscription source (batched all-stream reads).</param>
    /// <param name="checkpoints">The checkpoint sink (fenced compare-and-swap persistence).</param>
    /// <param name="hwmPoller">The safe high-water-mark poller (one read per tick, shared).</param>
    /// <param name="projections">The registered async projections to lead (all of them — single-process).</param>
    /// <param name="options">The Acta options carrying <see cref="ActaOptions.Daemon"/>.</param>
    /// <param name="logger">The logger for the error policy and lifecycle notices.</param>
    /// <param name="deadLetters">The shared, in-memory dead-letter buffer the error policy records poisoned events into (task 5.4).</param>
    /// <param name="gapGuard">The gap policy consulted when a projection's checkpoint is trapped under the safe HWM (task 5.3).</param>
    /// <param name="timeProvider">The clock for polling delays and dead-letter timestamps; <see langword="null"/> resolves to <see cref="TimeProvider.System"/> (V-1, mirrors <c>InMemoryEventStore</c>).</param>
    /// <exception cref="ArgumentNullException">A required collaborator is <see langword="null"/>.</exception>
    // timeProvider is the optional trailing parameter (V-1): the composition root does not register a
    // TimeProvider, so a required one would fault DI resolution; an optional default lets the daemon
    // resolve via ActivatorUtilities without one. Ordered after gapGuard/deadLetters because C#
    // requires optional parameters to be trailing (the only deviation from the plan §2.4 parameter
    // order — mirrors the same deviation already made for deadLetters in task 5.4).
    public ProjectionDaemon(
        ISubscriptionSource source,
        ICheckpointSink checkpoints,
        HwmPoller hwmPoller,
        IEnumerable<AsyncProjectionRegistration> projections,
        IOptions<ActaOptions> options,
        ILogger<ProjectionDaemon> logger,
        DeadLetterBuffer deadLetters,
        GapGuard gapGuard,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(hwmPoller);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(deadLetters);
        ArgumentNullException.ThrowIfNull(gapGuard);

        _source = source;
        _checkpoints = checkpoints;
        _hwmPoller = hwmPoller;
        _projections = [.. projections];
        _daemon = options.Value.Daemon;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _deadLetters = deadLetters;
        _gapGuard = gapGuard;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool anyCatchUp;
            try
            {
                anyCatchUp = await RunTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // Graceful stop: a batch in flight was cancelled; its checkpoint was not saved.
            }

            if (anyCatchUp)
            {
                continue; // Catch-up mode: skip the polling delay and drain the backlog immediately.
            }

            try
            {
                await Task.Delay(_daemon.PollingInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // Graceful stop while idle between ticks.
            }
        }
    }

    /// <summary>
    /// Runs one tick: reads the safe high-water mark once and drives every non-halted projection from
    /// its checkpoint. Extracted (<see langword="internal"/>) so tests drive it deterministically —
    /// without a background thread or a real clock (D9, <c>InternalsVisibleTo("Acta.Tests")</c>).
    /// </summary>
    /// <param name="ct">A token to cancel the tick (graceful stop propagates as <see cref="OperationCanceledException"/>).</param>
    /// <returns><see langword="true"/> when any projection is still in catch-up mode (the caller should skip the polling delay).</returns>
    internal async ValueTask<bool> RunTickAsync(CancellationToken ct)
    {
        // Step 0: one safe-HWM read per tick, shared across every projection (P×T → 1).
        var hwm = await _hwmPoller.ReadSafeHighWaterMarkAsync(ct).ConfigureAwait(false);

        var anyCatchUp = false;
        foreach (var registration in _projections)
        {
            if (registration.IsHalted)
            {
                continue; // Baseline error policy: a halted projection is no longer led (5.2).
            }

            await RunProjectionTickAsync(registration, hwm, ct).ConfigureAwait(false);

            if (registration.IsInCatchUp)
            {
                anyCatchUp = true;
            }
        }

        return anyCatchUp;
    }

    private async ValueTask RunProjectionTickAsync(AsyncProjectionRegistration registration, GlobalPosition hwm, CancellationToken ct)
    {
        // Trust the cached checkpoint; (re)load from the sink only at first lead or after a fence.
        var checkpoint = registration.CachedCheckpoint
            ?? await _checkpoints.LoadAsync(registration.Name, tenantId: null, ct).ConfigureAwait(false)
            ?? GlobalPosition.Start;

        if (checkpoint >= hwm)
        {
            registration.ExitCatchUp(); // Caught up to the global head — skip the batch read (P×T → 1).
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var batch = await _source
                .ReadBatchAsync(checkpoint, _daemon.BatchSize, registration.EventTypes, ct)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                // Checkpoint < hwm but no matching events remain — either a non-matching tail (V-2)
                // or a true GlobalPosition gap (task 5.3). Peek the RAW, unfiltered stream: a
                // non-empty peek means events exist above the checkpoint that this projection's
                // filter simply does not match (no gap); an empty peek means a true hole reaching
                // the HWM. One element is enough — existence, not content, is all the guard needs.
                var rawEventExistsAboveCheckpoint = false;
                await foreach (var _ in _source.ReadFromAsync(checkpoint, ct).ConfigureAwait(false))
                {
                    rawEventExistsAboveCheckpoint = true;
                    break;
                }

                var now = _timeProvider.GetUtcNow();
                var verdict = _gapGuard.Evaluate(checkpoint, hwm, rawEventExistsAboveCheckpoint, registration.GapObservedAt, now);

                if (verdict == GapVerdict.WaitSafeHarbor)
                {
                    registration.SetGapObserved(now); // Stable first-observed timestamp (no-op if already set).
                    registration.ExitCatchUp();
                    break;
                }

                if (verdict == GapVerdict.SkipPermanent)
                {
                    var gapFrom = checkpoint;
                    checkpoint = hwm; // Strictly forward — this branch only runs when checkpoint < hwm.

                    try
                    {
                        await _checkpoints
                            .SaveAsync(registration.Name, tenantId: null, checkpoint, _ownerToken, ct)
                            .ConfigureAwait(false);
                    }
                    catch (CheckpointFencedException)
                    {
                        // Same zombie-guard as the batch-apply path below: a fence means this daemon lost
                        // leadership, so stop leading the projection (its owner token is permanently stale)
                        // rather than rethrow (D8). Re-leading would re-fence every tick with the same token.
                        registration.DropLeadership();
                        registration.ExitCatchUp();
                        break;
                    }

                    registration.CacheCheckpoint(checkpoint);
                    _gapGuard.RecordSkip(registration.Name, gapFrom, hwm);
                    registration.ClearGapObserved();
                    registration.ExitCatchUp();
                    break;
                }

                // NoGap: the current V-2 behavior — no matching events remain up to the HWM → caught up.
                registration.ClearGapObserved();
                registration.ExitCatchUp();
                break;
            }

            // Per-event dispatch so the error policy (retry → dead-letter | pause) can act on a single
            // poisoned event without stopping the batch. The checkpoint is still saved ONCE per batch
            // boundary (lastGood) — never per event — to preserve the 5.2 CAS write cadence.
            var lastGood = checkpoint;
            var paused = false;
            foreach (var stored in batch)
            {
                var outcome = await ApplyWithPolicyAsync(registration, stored, ct).ConfigureAwait(false);
                if (outcome == ApplyOutcome.Paused)
                {
                    paused = true; // Pause: stop leading this projection; do NOT advance past the poisoned event.
                    break;
                }

                // Applied, or dead-lettered-and-skipped — both advance the checkpoint past this event.
                lastGood = stored.GlobalPosition;
            }

            if (lastGood > checkpoint)
            {
                checkpoint = lastGood;

                try
                {
                    await _checkpoints
                        .SaveAsync(registration.Name, tenantId: null, checkpoint, _ownerToken, ct)
                        .ConfigureAwait(false);
                }
                catch (CheckpointFencedException)
                {
                    // Zombie-guard (Postgres readiness; the in-memory sink never fences — D8): a fence
                    // means this daemon lost leadership, so stop leading the projection — re-leading would
                    // re-apply events and re-fence every tick with the same permanently-stale owner token.
                    // The daemon does not rethrow.
                    registration.DropLeadership();
                    registration.ExitCatchUp();
                    break;
                }

                registration.CacheCheckpoint(checkpoint);
            }

            if (paused)
            {
                registration.ExitCatchUp(); // Paused projection is no longer led — checkpoint stayed before the poison.
                break;
            }

            if (batch.Count < _daemon.BatchSize)
            {
                registration.ExitCatchUp(); // Partial batch → matching backlog exhausted → caught up.
                break;
            }

            // Full batch — more matching events may remain. A large pending backlog keeps the
            // projection in catch-up (one batch per tick, drained without the polling delay).
            var pending = hwm.Value - checkpoint.Value;
            if (pending > _daemon.PendingEventsThreshold)
            {
                registration.EnterCatchUp();
                break;
            }

            registration.ExitCatchUp(); // Full batch under threshold — keep draining within this tick.
        }
    }

    /// <summary>The result of applying one event under a projection's <see cref="ProjectionErrorPolicy"/>.</summary>
    private enum ApplyOutcome
    {
        /// <summary>The event was applied successfully (possibly after one or more retries).</summary>
        Applied,

        /// <summary>Retries were exhausted and the policy skipped the event (<see cref="ErrorAction.DeadLetterAndSkip"/>); the checkpoint advances past it.</summary>
        Skipped,

        /// <summary>Retries were exhausted and the policy paused the projection (<see cref="ErrorAction.Pause"/>); the checkpoint does not advance past the event.</summary>
        Paused,
    }

    /// <summary>
    /// Applies one event to a projection under its <see cref="ProjectionErrorPolicy"/>: dispatch through
    /// the reused runner (a single-element batch keeps its ordering + mark-after-apply idempotency), and
    /// on a persistent failure retry up to <see cref="ProjectionErrorPolicy.MaxRetries"/> times before
    /// recording the poisoned event in the <see cref="DeadLetterBuffer"/> and acting on
    /// <see cref="ProjectionErrorPolicy.OnApplyError"/>. A retry is safe because the runner never advances
    /// its watermark past an event whose apply threw, so it only ever re-applies the still-failing event
    /// (never re-applies an already-applied one).
    /// </summary>
    /// <param name="registration">The projection being led, carrying its runner and error policy.</param>
    /// <param name="stored">The single event to apply.</param>
    /// <param name="ct">A token to observe for cancellation (graceful stop rethrows before any policy handling).</param>
    /// <returns>Whether the event was applied, dead-lettered-and-skipped, or the projection was paused.</returns>
    private async ValueTask<ApplyOutcome> ApplyWithPolicyAsync(
        AsyncProjectionRegistration registration, StoredEvent stored, CancellationToken ct)
    {
        var policy = registration.ErrorPolicy;
        IReadOnlyList<StoredEvent> single = [stored];
        var attempts = 0;

        while (true)
        {
            try
            {
                await registration.Runner.RunAsync(single, ct).ConfigureAwait(false);
                return ApplyOutcome.Applied;
            }
            catch (OperationCanceledException)
            {
                throw; // Graceful stop — propagate; not an apply error, no checkpoint saved for the unfinished batch.
            }
#pragma warning disable CA1031 // Error policy (5.4): a projection apply may throw anything; retry → dead-letter | pause (never a daemon crash) is the deliberate contract (ADR-005).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                attempts++; // Total apply attempts so far (the initial attempt plus every retry).
                if (attempts <= policy.MaxRetries)
                {
                    continue; // Retry in place — idempotent via the runner's mark-after-apply watermark.
                }

                // Retries exhausted. Record the poisoned event (type + message only, never the
                // payload/event/metadata — ADR-008/017) into the shared dead-letter buffer.
                _deadLetters.Record(registration.Name, tenantId: null, stored.GlobalPosition, attempts, ex, _timeProvider.GetUtcNow());

                if (policy.OnApplyError == ErrorAction.Pause)
                {
                    registration.Halt();

                    // Log the exception (type + message) only — see the DeadLetterBuffer caveat: neither
                    // this log nor the entry can scrub a caller-authored exception message.
                    _logger.LogError(
                        ex,
                        "Async projection {ProjectionName} paused after {Attempts} failed apply attempts at position {Position}; the daemon and other projections continue.",
                        registration.Name,
                        attempts,
                        stored.GlobalPosition.Value);
                    return ApplyOutcome.Paused;
                }

                _logger.LogError(
                    ex,
                    "Async projection {ProjectionName} dead-lettered and skipped position {Position} after {Attempts} failed apply attempts; the projection and daemon continue.",
                    registration.Name,
                    stored.GlobalPosition.Value,
                    attempts);
                return ApplyOutcome.Skipped;
            }
        }
    }
}
