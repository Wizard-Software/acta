namespace Acta.Abstractions;

/// <summary>
/// The action a projection's error policy takes once <see cref="ProjectionErrorPolicy.MaxRetries"/>
/// retries of a poisoned event have been exhausted (task 5.4, MODULE-INTERFACES Group 5, decision
/// D5 — a Marten-style philosophy: skip past a poisoned event, or pause just that one projection).
/// </summary>
public enum ErrorAction
{
    /// <summary>
    /// Record the poisoned event in the shared dead-letter buffer and advance the checkpoint past
    /// it — the projection and the daemon keep running (the default).
    /// </summary>
    DeadLetterAndSkip,

    /// <summary>
    /// Record the poisoned event in the shared dead-letter buffer and halt THIS projection only —
    /// the checkpoint is not advanced past the poisoned event, so a rebuild or a manual resume
    /// retries it. The daemon and every other projection keep running (ADR-005 — one poisoned event
    /// never stops the daemon).
    /// </summary>
    Pause,
}

/// <summary>
/// Per-projection error policy (task 5.4, MODULE-INTERFACES Group 5): how many times to retry a
/// poisoned event's apply before giving up, and what to do once retries are exhausted.
/// </summary>
/// <param name="OnApplyError">
/// The action taken once <see cref="MaxRetries"/> retries are exhausted. Defaults to
/// <see cref="ErrorAction.DeadLetterAndSkip"/> — a single poisoned event never halts a projection by
/// default (ADR-005).
/// </param>
/// <param name="MaxRetries">
/// The number of retry attempts made after the first failed apply, so a poisoned event is applied at
/// most <c>MaxRetries + 1</c> times in total before <see cref="OnApplyError"/> fires. Retries are
/// safe because the runner's mark-after-apply idempotency watermark means a retried apply only ever
/// re-applies the still-unwatermarked poisoned event. Must be non-negative (a negative value is a
/// configuration error, rejected fail-fast at construction — see the remarks). Defaults to <c>3</c>.
/// </param>
/// <param name="GapSafeHarborTimeout">
/// The safe-harbor wait a gap guard grants a missing <see cref="GlobalPosition"/> before declaring it
/// a permanent gap. This record owns the field (task 5.4), but only the gap guard (task 5.3)
/// consumes it — this task does not read it. <see langword="null"/> falls back to the daemon-wide
/// default (<c>ProjectionDaemonOptions.GapSafeHarborTimeout</c>).
/// </param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="MaxRetries"/> is negative.</exception>
/// <remarks>
/// Decision D4: a negative <see cref="MaxRetries"/> is rejected fail-fast at construction time
/// (<see cref="ArgumentOutOfRangeException"/>) rather than silently clamped to zero — consistent with
/// CONSTITUTION §1.3 ("fail-fast configuration at host startup; zero validation on the hot path"). A
/// silent clamp would be a more surprising outcome than an immediate, loud failure.
/// </remarks>
public sealed record ProjectionErrorPolicy(
    ErrorAction OnApplyError = ErrorAction.DeadLetterAndSkip,
    int MaxRetries = 3,
    TimeSpan? GapSafeHarborTimeout = null)
{
    /// <summary>
    /// The number of retry attempts made after the first failed apply. Validated non-negative at
    /// construction (D4 — fail-fast, never a silent clamp).
    /// </summary>
    public int MaxRetries { get; } = NonNegative(MaxRetries);

    private static int NonNegative(int maxRetries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);
        return maxRetries;
    }
}
