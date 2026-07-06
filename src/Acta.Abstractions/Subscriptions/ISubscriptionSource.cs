namespace Acta.Abstractions;

/// <summary>
/// Catch-up subscription source (FR-6) — the canonical read path a projection daemon uses to
/// consume the all-stream in explicit batches. Implemented in the core over
/// <see cref="IEventStore.ReadAllAsync"/> (in-memory, Tier 1) and in the Postgres adapter.
/// <para>
/// ADR-015: <see cref="ReadBatchAsync"/> is the <b>canonical daemon path</b> — an explicit batch
/// limit, the safe high-water-mark guard (visibility-lag cutback) inherited from the underlying
/// store's read contract, and event-type filter <i>pushdown</i> to the backend (Postgres:
/// <c>WHERE event_type = ANY(@types)</c>) rather than client-side filtering of fully-materialized
/// payloads, which would amplify the read by a factor of P (the projection's type selectivity).
/// </para>
/// </summary>
public interface ISubscriptionSource
{
    /// <summary>
    /// Streams the all-stream forward from the exclusive lower bound <paramref name="from"/> in
    /// ascending <see cref="GlobalPosition"/> order — the live, unbounded read path. The port
    /// carries no event-type filter here: filtering the live stream is the daemon's concern.
    /// </summary>
    /// <param name="from">
    /// The exclusive lower bound — the checkpoint of the last position already consumed.
    /// <see cref="GlobalPosition.Start"/> streams from the very beginning.
    /// </param>
    /// <param name="ct">A token to cancel the enumeration.</param>
    /// <returns>
    /// The all-stream events after <paramref name="from"/>, ascending by <see cref="GlobalPosition"/>.
    /// </returns>
    IAsyncEnumerable<StoredEvent> ReadFromAsync(GlobalPosition from, CancellationToken ct = default);

    /// <summary>
    /// Reads a single catch-up batch guaranteeing: only events at or below the safe high-water
    /// mark (visibility-lag cutback, inherited from the store's read contract); at most
    /// <paramref name="maxCount"/> events; and — when <paramref name="eventTypes"/> is supplied —
    /// only those types (pushed down to the backend). Returns an empty batch when no safe, matching
    /// events remain after <paramref name="from"/>, leaving the polling/backoff decision to the
    /// daemon.
    /// </summary>
    /// <param name="from">
    /// The exclusive lower bound — the checkpoint of the last position already consumed.
    /// <see cref="GlobalPosition.Start"/> reads from the very beginning.
    /// </param>
    /// <param name="maxCount">
    /// The maximum number of <i>matching</i> events to return; must be strictly greater than zero.
    /// The limit counts events that pass <paramref name="eventTypes"/>, not raw events scanned.
    /// </param>
    /// <param name="eventTypes">
    /// The event-type filter: <see langword="null"/> matches every type; an empty set matches none
    /// (yielding an empty batch); otherwise only events whose <see cref="StoredEvent.EventType"/>
    /// is contained in the set.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// The next batch of safe, matching events in ascending <see cref="GlobalPosition"/> order
    /// (possibly empty).
    /// </returns>
    ValueTask<IReadOnlyList<StoredEvent>> ReadBatchAsync(
        GlobalPosition from,
        int maxCount,
        IReadOnlySet<string>? eventTypes = null,
        CancellationToken ct = default);
}
