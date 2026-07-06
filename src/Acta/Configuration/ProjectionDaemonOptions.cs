namespace Acta.Configuration;

/// <summary>
/// Configuration for the asynchronous projection daemon (task 5.2, 03-contracts.md §2). Attached
/// additively to <see cref="ActaOptions.Daemon"/> now that a reader exists (the daemon introduced by
/// Feature 5) — the non-breaking additive shape foreshadowed by <see cref="ActaOptions"/>'s remarks
/// (decision D1). All five fields materialize here (the frozen 03-contracts §2 shape); task 5.2
/// consumes four of them — <see cref="BatchSize"/>, <see cref="PendingEventsThreshold"/>,
/// <see cref="PollingInterval"/>, and <see cref="VisibilityLag"/> — while
/// <see cref="GapSafeHarborTimeout"/> is carried for the gap guard (task 5.3).
/// <para>
/// Every field is validated fail-fast at host startup by <c>ActaOptionsValidator</c>
/// (<c>ValidateOnStart</c>): a non-positive <see cref="BatchSize"/>/<see cref="PendingEventsThreshold"/>,
/// a non-positive <see cref="PollingInterval"/>/<see cref="VisibilityLag"/>, or a negative
/// <see cref="GapSafeHarborTimeout"/> throws <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>
/// before the host serves traffic.
/// </para>
/// </summary>
public sealed class ProjectionDaemonOptions
{
    /// <summary>
    /// The maximum number of matching events a single catch-up read (<c>ISubscriptionSource.ReadBatchAsync</c>)
    /// returns per batch. Must be strictly positive. Defaults to <c>500</c>.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Backpressure threshold: when a projection's pending backlog (safe high-water mark minus its
    /// checkpoint) exceeds this many positions after a full batch, the daemon keeps that projection
    /// in catch-up mode — draining without the inter-tick polling delay until the matching backlog
    /// is exhausted. Must be strictly positive. Defaults to <c>5000</c> (ESDB precedent).
    /// </summary>
    public int PendingEventsThreshold { get; set; } = 5000;

    /// <summary>
    /// The safe-harbor wait a gap guard grants a missing <see cref="Abstractions.GlobalPosition"/>
    /// before declaring it a permanent gap. Carried for task 5.3 (not consumed in 5.2). Must be
    /// non-negative. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan GapSafeHarborTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The idle wait between polling ticks when no projection is in catch-up mode. Must be strictly
    /// positive. Defaults to 200 milliseconds.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The safe high-water-mark cutback: events younger than <c>now - VisibilityLag</c> are withheld
    /// from catch-up reads to avoid consuming positions a not-yet-committed transaction may still
    /// fill (out-of-order visibility, ADR-001). For the in-memory backend the effective cutback is
    /// zero (positions are assigned and published under the same write lock as the append), so this
    /// value is dormant until the Postgres backend (Feature 7). Must be strictly positive
    /// (03-contracts §3). Defaults to 5 seconds.
    /// </summary>
    public TimeSpan VisibilityLag { get; set; } = TimeSpan.FromSeconds(5);
}
