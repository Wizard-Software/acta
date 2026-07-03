namespace Acta.Abstractions;

/// <summary>
/// Port for appending events to, and reading events from, the event store (FR-1/FR-3). Backend
/// implementations live in the core (<c>InMemoryEventStore</c>) and in the Postgres adapter
/// (<c>PostgresEventStore</c>); both implement the exact same append/read contract described
/// here, verified by a shared contract test suite (03-contracts.md, ADR-015).
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Atomically appends <paramref name="events"/> to <paramref name="streamId"/> (NFR-2): the
    /// whole batch becomes visible in a single, all-or-nothing step — callers never observe a
    /// partially-applied batch.
    /// <para>
    /// <paramref name="expectedVersion"/> is validated against the guard matrix defined by
    /// <see cref="ExpectedVersion"/> (ADR-003, 03-contracts.md §1); a violation throws
    /// <see cref="ConcurrencyException"/>.
    /// </para>
    /// <para>
    /// Deduplication is keyed on <c>(streamId, EventId)</c> and is unconditional, independent of
    /// <paramref name="expectedVersion"/>: a duplicate never throws — it always yields an
    /// idempotent success (<see cref="AppendResult.Deduplicated"/> = <see langword="true"/>),
    /// even when the concurrency guard above would otherwise have failed (D3, ADR-003).
    /// </para>
    /// </summary>
    /// <param name="streamId">Identifier of the target stream.</param>
    /// <param name="expectedVersion">
    /// The optimistic-concurrency guard — one of the <see cref="ExpectedVersion"/> sentinels, or
    /// an exact expected version <c>&gt;= 1</c>.
    /// </param>
    /// <param name="events">The events to append, in order.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The <see cref="AppendResult"/> describing the outcome of the append.</returns>
    /// <exception cref="ConcurrencyException">
    /// <paramref name="expectedVersion"/>'s guard was violated by a genuinely new (non-duplicate)
    /// batch.
    /// </exception>
    ValueTask<AppendResult> AppendAsync(
        string streamId,
        long expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the events of <paramref name="streamId"/> whose <see cref="StoredEvent.Version"/>
    /// falls within <c>[<paramref name="fromVersion"/>, <paramref name="toVersion"/>]</c>
    /// (inclusive), yielded in <paramref name="direction"/> order (FR-3). A
    /// <see langword="null"/> <paramref name="toVersion"/> reads to the end of the stream;
    /// setting it enables a point-in-time read. A non-existent stream yields an empty sequence.
    /// </summary>
    /// <param name="streamId">Identifier of the stream to read.</param>
    /// <param name="fromVersion">The inclusive lower bound of the version range (0-based).</param>
    /// <param name="toVersion">
    /// The inclusive upper bound of the version range, or <see langword="null"/> for no upper
    /// bound.
    /// </param>
    /// <param name="direction">The order in which to yield events.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The matching events of the stream, in <paramref name="direction"/> order.</returns>
    IAsyncEnumerable<StoredEvent> ReadStreamAsync(
        string streamId,
        long fromVersion = 0,
        long? toVersion = null,
        Direction direction = Direction.Forwards,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the all-stream (every stream combined, ordered by <see cref="GlobalPosition"/>)
    /// starting after <paramref name="from"/> — the foundation of catch-up subscriptions
    /// (<c>ISubscriptionSource</c>).
    /// <para>
    /// ADR-015: the high-water-mark guard is part of this port's contract, not an
    /// implementation-specific detail — no conforming implementation may ever return an event
    /// above its safe, visibility-lag-adjusted high-water mark. <paramref name="upTo"/> bounds
    /// the read from above (an inclusive point-in-time read); <paramref name="maxCount"/> bounds
    /// the size of the returned batch. Both backends (in-memory and Postgres) MUST pass the same
    /// guard contract test.
    /// </para>
    /// </summary>
    /// <param name="from">
    /// The exclusive lower bound — the checkpoint of the last position already consumed
    /// (catch-up semantics). <see cref="GlobalPosition.Start"/> reads from the very beginning.
    /// </param>
    /// <param name="upTo">
    /// The inclusive upper bound, or <see langword="null"/> for no upper bound.
    /// </param>
    /// <param name="maxCount">
    /// The maximum number of events to return, or <see langword="null"/> for no limit.
    /// </param>
    /// <param name="direction">The order in which to yield events.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The matching events of the all-stream, in <paramref name="direction"/> order.</returns>
    IAsyncEnumerable<StoredEvent> ReadAllAsync(
        GlobalPosition from,
        GlobalPosition? upTo = null,
        int? maxCount = null,
        Direction direction = Direction.Forwards,
        CancellationToken ct = default);
}
