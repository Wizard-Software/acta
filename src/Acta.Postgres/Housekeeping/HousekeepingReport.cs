namespace Acta.Postgres.Housekeeping;

/// <summary>
/// The outcome of one <see cref="Housekeeper.SweepAsync"/> pass: whether this instance held the
/// single-active advisory lock and actually ran the sweep, plus the per-table purge counts. Returned
/// so the hosted service can log/emit metrics and the integration tests can assert exact row counts.
/// </summary>
/// <param name="Executed">
/// <see langword="true"/> when this instance acquired the <c>{schema}:housekeeping</c> advisory lock
/// and ran the DELETEs; <see langword="false"/> when another pod already held the lock (this pass was
/// skipped — single-active).
/// </param>
/// <param name="OutboxPurged">Published outbox rows deleted (0 when the outbox purge is disabled).</param>
/// <param name="IdempotencyPurged">Expired idempotency rows deleted.</param>
/// <param name="ReservationsPurged">Expired, unconfirmed reservation rows deleted (0 when disabled).</param>
/// <param name="DeadLetterPurged">Aged dead-letter rows deleted (0 when disabled).</param>
public sealed record HousekeepingReport(
    bool Executed,
    long OutboxPurged,
    long IdempotencyPurged,
    long ReservationsPurged,
    long DeadLetterPurged)
{
    /// <summary>A pass that did not run because another pod held the single-active lock.</summary>
    public static readonly HousekeepingReport Skipped = new(false, 0, 0, 0, 0);

    /// <summary>The total rows purged across every auxiliary table in this pass.</summary>
    public long TotalPurged => OutboxPurged + IdempotencyPurged + ReservationsPurged + DeadLetterPurged;
}
