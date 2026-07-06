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
/// of <see cref="ProjectionDaemonOptions.BatchSize"/> from its checkpoint — dispatch each batch
/// through the reused <c>InlineProjectionRunner</c> (ordered, idempotent apply) and advance the
/// checkpoint via a fenced <c>ICheckpointSink.SaveAsync</c>.
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
/// <b>Baseline error policy (5.2; full policy = 5.4).</b> An exception from a projection's apply
/// halts THAT projection (logged at <see cref="LogLevel.Error"/> with the exception type and
/// message only — never the payload/event/metadata, ADR-008/017) while the daemon and every other
/// projection continue (ADR-005 — one poisoned event never stops the daemon). Task 5.4 replaces the
/// halt with retry → dead-letter | pause.
/// <b>Caveat:</b> the daemon logs the exception verbatim, so a host projection whose own
/// <c>ApplyAsync</c> builds an exception message from record data (e.g. <c>$"failed for {order.Email}"</c>)
/// surfaces that text to the log sink — the library adds no payload but cannot scrub a caller-authored
/// message; hosts must keep PII out of their exception messages (mirrors <c>InlineProjectionRunner</c>).
/// </para>
/// <para>
/// <b>Graceful stop.</b> Cancellation (host stop) ends the loop after the current batch — a batch
/// cancelled in flight does not save its checkpoint, so the events replay safely on restart
/// (idempotent, at-least-once). A <see cref="CheckpointFencedException"/> drops leadership of that
/// projection and reloads its checkpoint next tick (Postgres readiness; the in-memory sink never
/// fences, D8).
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
    /// <param name="logger">The logger for the baseline error policy and lifecycle notices.</param>
    /// <param name="timeProvider">The clock for polling delays; <see langword="null"/> resolves to <see cref="TimeProvider.System"/> (V-1, mirrors <c>InMemoryEventStore</c>).</param>
    /// <exception cref="ArgumentNullException">A required collaborator is <see langword="null"/>.</exception>
    // timeProvider is the optional trailing parameter (V-1): the composition root does not register a
    // TimeProvider, so a required one would fault DI resolution; an optional default lets the daemon
    // resolve via ActivatorUtilities without one. Ordered after logger because C# requires optional
    // parameters to be trailing (the only deviation from the plan §2.4 parameter order).
    public ProjectionDaemon(
        ISubscriptionSource source,
        ICheckpointSink checkpoints,
        HwmPoller hwmPoller,
        IEnumerable<AsyncProjectionRegistration> projections,
        IOptions<ActaOptions> options,
        ILogger<ProjectionDaemon> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(hwmPoller);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _checkpoints = checkpoints;
        _hwmPoller = hwmPoller;
        _projections = [.. projections];
        _daemon = options.Value.Daemon;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
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
                registration.ExitCatchUp(); // V-2: no matching events remain up to the HWM → caught up.
                break;
            }

            try
            {
                await registration.Runner.RunAsync(batch, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // Graceful stop — do not save the checkpoint for the unfinished batch (safe, idempotent replay).
            }
#pragma warning disable CA1031 // Baseline error policy (5.2): a projection apply may throw anything; halting THIS projection (not the daemon) is the deliberate contract (ADR-005). Full retry/dead-letter/pause = 5.4.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                registration.Halt();
                registration.ExitCatchUp();

                // Log the exception (type + message) only — NEVER the payload/event/metadata (ADR-008/017).
                _logger.LogError(
                    ex,
                    "Async projection {ProjectionName} halted after a failed apply; the daemon and other projections continue.",
                    registration.Name);
                break;
            }

            checkpoint = batch[^1].GlobalPosition;

            try
            {
                await _checkpoints
                    .SaveAsync(registration.Name, tenantId: null, checkpoint, _ownerToken, ct)
                    .ConfigureAwait(false);
            }
            catch (CheckpointFencedException)
            {
                // Zombie-guard (Postgres readiness; the in-memory sink never fences — D8): drop
                // leadership and reload the checkpoint next tick. The daemon does not rethrow.
                registration.DropLeadership();
                registration.ExitCatchUp();
                break;
            }

            registration.CacheCheckpoint(checkpoint);

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
}
