namespace Acta.Postgres.Configuration;

/// <summary>
/// Retention and cleanup policy for the auxiliary PostgreSQL tables (03-contracts §2, 04-data §3.6;
/// ADR-016). Owned by the housekeeping loop (<see cref="Acta.Postgres.Housekeeping.Housekeeper"/>,
/// driven by <c>HousekeeperHostedService</c>) — a single-active sweep that purges published outbox
/// rows, expired idempotency and reservation entries, and aged dead-letter rows.
/// <para>
/// Every value is an <b>explicit</b> retention policy. <see cref="System.TimeSpan.Zero"/> disables the
/// corresponding purge (a deliberate host decision — that table then becomes the host's operational
/// responsibility), and a non-positive <see cref="Interval"/> disables the housekeeping loop entirely.
/// Retention is evaluated against the PostgreSQL server clock (<c>now()</c>), never a client clock.
/// </para>
/// </summary>
public sealed class HousekeepingOptions
{
    /// <summary>
    /// The cadence of the housekeeping loop (default 5 minutes). A non-positive value disables the
    /// loop — the hosted service logs a startup notice and never sweeps (04-data §3.6: cleanup is then
    /// the host's operational responsibility).
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a <b>published</b> outbox row is retained before it is purged (default 7 days). The
    /// sweep deletes rows whose <c>published_at IS NOT NULL AND published_at &lt; now() - value</c>;
    /// unpublished rows (<c>published_at IS NULL</c>) are never purged. <see cref="System.TimeSpan.Zero"/>
    /// disables the outbox purge.
    /// </summary>
    public TimeSpan PublishedOutboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a dead-letter row is retained after it first failed before it is purged (default
    /// 30 days). The sweep deletes rows whose <c>first_failed_at &lt; now() - value</c>.
    /// <see cref="System.TimeSpan.Zero"/> disables the dead-letter purge.
    /// </summary>
    public TimeSpan DeadLetterRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The grace period past a reservation's expiry before an unconfirmed, expired reservation is swept
    /// (default 1 hour). The sweep deletes rows whose <c>confirmed = false AND expires_at &lt; now() -
    /// value</c> — confirmed reservations (<c>expires_at IS NULL</c>) are never swept.
    /// <see cref="System.TimeSpan.Zero"/> disables the reservation sweep.
    /// </summary>
    public TimeSpan ExpiredReservationsSweep { get; set; } = TimeSpan.FromHours(1);

    // Idempotency has no retention knob here: the sweep deletes rows whose expires_at < now(); the
    // per-entry retention is set by IIdempotencyStore.TryRegisterAsync(retention) (03-contracts §2).
}
