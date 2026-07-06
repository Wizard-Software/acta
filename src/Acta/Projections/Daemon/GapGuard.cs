using Microsoft.Extensions.Logging;

using Acta.Abstractions;
using Acta.Configuration;

namespace Acta.Projections.Daemon;

/// <summary>
/// The gap policy for a projection whose checkpoint is trapped under the safe high-water mark
/// (task 5.3, ADR-001 R3): a pure decision function (<see cref="Evaluate"/>) plus its side effect
/// (<see cref="RecordSkip"/>), kept deliberately separate so the decision stays trivially unit- and
/// property-testable (task 5.5) — <see cref="Evaluate"/> performs no I/O and touches no mutable
/// state; it is driven entirely by its parameters.
/// <para>
/// <b>Visibility (decision D6, corrected).</b> The class itself is <see langword="public"/> — not the
/// <see langword="internal"/> originally proposed — because it is a required constructor parameter of
/// the <see langword="public"/> <see cref="ProjectionDaemon"/>: a public member cannot expose a less
/// accessible type in its signature (CS0051). This mirrors <see cref="DeadLetterBuffer"/> (task 5.4),
/// threaded through the very same constructor for the very same reason. <see cref="Evaluate"/> and
/// <see cref="RecordSkip"/> themselves stay <see langword="internal"/> — the actual usable surface
/// this decision meant to keep off the public API is these two members, not the type name.
/// </para>
/// <para>
/// <b>Gap vs. non-matching tail (decision D4).</b> A projection whose checkpoint sits below the safe
/// HWM either faces a true hole in <see cref="GlobalPosition"/> (some position in between will never
/// be filled) or a non-matching tail — trailing events above the checkpoint that simply do not pass
/// this projection's event-type filter (correction V-2). The caller (<see cref="ProjectionDaemon"/>)
/// distinguishes the two with a cheap raw-stream peek and passes the result in as
/// <c>rawEventExistsAboveCheckpoint</c>: a non-empty peek means a non-matching tail, not a gap — this
/// guard must not count it, or <c>acta.projection.gaps_skipped</c> would over-count and V-2's
/// steady-state behavior (no busy-spin on a type-selective projection) would regress.
/// </para>
/// <para>
/// <b>Safe-harbor timer (decision D5).</b> A true gap is not skipped on first sight — the daemon
/// grants it <see cref="ProjectionDaemonOptions.GapSafeHarborTimeout"/> to resolve on its own (a
/// still-in-flight write may yet fill it) before this guard treats it as permanent. The elapsed time
/// is measured from <c>gapFirstObservedAt</c> (state the caller tracks per projection, first set the
/// tick this gap was noticed) to <c>now</c>: not yet elapsed → <see cref="GapVerdict.WaitSafeHarbor"/>;
/// elapsed → <see cref="GapVerdict.SkipPermanent"/>. A <see cref="ProjectionDaemonOptions.GapSafeHarborTimeout"/>
/// of <see cref="TimeSpan.Zero"/> collapses this to an immediate skip even on the very first
/// observation — the expression of ADR-001's "a gap already known to be older than the safe HWM's
/// visibility-lag cutback is skipped immediately, without waiting" for a backend (Tier-1 in-memory)
/// whose cutback is dormant (<see cref="HwmPoller"/>'s remarks) and therefore grants no additional
/// grace period beyond what the caller configures here.
/// </para>
/// </summary>
public sealed class GapGuard
{
    private readonly ProjectionDaemonOptions _options;
    private readonly ProjectionDaemonMetrics _metrics;
    private readonly ILogger<GapGuard> _logger;

    // Carried for architectural symmetry with the daemon's optional-trailing-TimeProvider pattern
    // (V-1) — Evaluate is pure over its own now parameter and never consults the clock directly, and
    // RecordSkip performs no I/O needing one either. Reserved for a future diagnostic timestamp.
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Constructs the guard from its collaborators. <paramref name="timeProvider"/> is optional and
    /// falls back to <see cref="TimeProvider.System"/> — mirrors <see cref="ProjectionDaemon"/>'s own
    /// optional-clock pattern (V-1) so a required parameter here would not fault DI resolution.
    /// </summary>
    /// <param name="options">The daemon options carrying <see cref="ProjectionDaemonOptions.GapSafeHarborTimeout"/>.</param>
    /// <param name="metrics">The shared metrics owner incrementing <c>acta.projection.gaps_skipped</c>.</param>
    /// <param name="logger">The logger for the diagnostic gap-skip warning (never the event payload — ADR-008/017).</param>
    /// <param name="timeProvider">Reserved for future use; <see langword="null"/> resolves to <see cref="TimeProvider.System"/> (V-1).</param>
    /// <exception cref="ArgumentNullException">A required collaborator is <see langword="null"/>.</exception>
    public GapGuard(
        ProjectionDaemonOptions options,
        ProjectionDaemonMetrics metrics,
        ILogger<GapGuard> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _metrics = metrics;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Decides what a projection whose checkpoint sits below <paramref name="safeHwm"/> should do,
    /// after a matching-event batch read came back empty. Pure and deterministic: the same inputs
    /// always yield the same verdict, and no state is read or written (task 5.5 property tests drive
    /// this directly).
    /// </summary>
    /// <param name="checkpoint">The projection's current checkpoint.</param>
    /// <param name="safeHwm">The safe high-water mark shared across the tick (<see cref="HwmPoller"/>).</param>
    /// <param name="rawEventExistsAboveCheckpoint">
    /// Whether a raw, unfiltered event exists above <paramref name="checkpoint"/> (a cheap peek of the
    /// live stream, decision D4) — <see langword="true"/> means a non-matching tail (V-2), not a gap.
    /// </param>
    /// <param name="gapFirstObservedAt">
    /// The wall-clock time this exact gap was first observed (tracked per projection by the caller);
    /// <see langword="null"/> means this tick is the first observation.
    /// </param>
    /// <param name="now">The current wall-clock time (the caller's <see cref="TimeProvider.GetUtcNow"/>).</param>
    /// <returns>The verdict: <see cref="GapVerdict.NoGap"/>, <see cref="GapVerdict.WaitSafeHarbor"/>, or <see cref="GapVerdict.SkipPermanent"/>.</returns>
    internal GapVerdict Evaluate(
        GlobalPosition checkpoint,
        GlobalPosition safeHwm,
        bool rawEventExistsAboveCheckpoint,
        DateTimeOffset? gapFirstObservedAt,
        DateTimeOffset now)
    {
        if (checkpoint >= safeHwm)
        {
            return GapVerdict.NoGap; // Caught up — nothing to skip.
        }

        if (rawEventExistsAboveCheckpoint)
        {
            return GapVerdict.NoGap; // A non-matching tail (V-2), not a true hole — do not count it.
        }

        // A true hole reaching the safe HWM. Elapsed time since first observed (zero on the very
        // first tick) decides whether the safe-harbor window has run out (D5).
        var elapsedSinceObserved = gapFirstObservedAt is null ? TimeSpan.Zero : now - gapFirstObservedAt.Value;

        return elapsedSinceObserved >= _options.GapSafeHarborTimeout
            ? GapVerdict.SkipPermanent
            : GapVerdict.WaitSafeHarbor;
    }

    /// <summary>
    /// Records a permanent gap skip: increments <c>acta.projection.gaps_skipped</c> (tagged with
    /// <paramref name="projectionName"/>) and logs a structured diagnostic warning — the trail
    /// property (4) of 05-implementation.md §3 requires ("a gap skip always leaves a diagnostic
    /// trail"). Carries only the projection name and <see cref="GlobalPosition"/> values — never the
    /// event payload or metadata (ADR-008/017, mirrors <see cref="DeadLetterBuffer"/>'s own contract).
    /// </summary>
    /// <param name="projectionName">The projection whose checkpoint was advanced past the gap.</param>
    /// <param name="gapFrom">The checkpoint the gap was skipped from (exclusive).</param>
    /// <param name="gapTo">The position the checkpoint was advanced to (the safe HWM, inclusive).</param>
    /// <exception cref="ArgumentException"><paramref name="projectionName"/> is null or empty.</exception>
    internal void RecordSkip(string projectionName, GlobalPosition gapFrom, GlobalPosition gapTo)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);

        _metrics.RecordGapSkipped(projectionName);
        _logger.LogWarning(
            "Async projection {ProjectionName} skipped a permanent GlobalPosition gap ({GapFrom}, {GapTo}]; advancing checkpoint past it.",
            projectionName,
            gapFrom.Value,
            gapTo.Value);
    }
}
