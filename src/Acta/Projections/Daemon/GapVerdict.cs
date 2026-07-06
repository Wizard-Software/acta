namespace Acta.Projections.Daemon;

/// <summary>
/// The verdict <see cref="GapGuard.Evaluate"/> returns for a projection whose checkpoint is trapped
/// under the safe high-water mark (HWM) after a matching-event batch came back empty (task 5.3,
/// ADR-001 R3 — <see cref="Abstractions.GlobalPosition"/> gaps are the norm, not an error).
/// </summary>
internal enum GapVerdict
{
    /// <summary>
    /// No gap to act on: the checkpoint has caught up to the safe HWM, or the space between them is
    /// a non-matching tail (correction V-2 — a type-selective projection whose trailing events do
    /// not pass its event-type filter) rather than a true hole in
    /// <see cref="Abstractions.GlobalPosition"/>.
    /// </summary>
    NoGap,

    /// <summary>
    /// A true gap reaching the safe HWM has outlasted its safe-harbor window (or none was granted) —
    /// skip it now: <see cref="GapGuard.RecordSkip"/> advances the checkpoint past the gap, increments
    /// the <c>acta.projection.gaps_skipped</c> counter, and logs a diagnostic warning.
    /// </summary>
    SkipPermanent,

    /// <summary>
    /// A true gap reaching the safe HWM was only just observed, or is still within its safe-harbor
    /// window — wait; a still-in-flight write may yet fill it before a later tick re-evaluates.
    /// </summary>
    WaitSafeHarbor,
}
