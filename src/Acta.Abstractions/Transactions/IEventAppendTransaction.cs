namespace Acta.Abstractions;

/// <summary>
/// The unit of atomicity for the "append + outbox" single-commit seam (ADR-002, FR-14): domain
/// events appended through this transaction and integration events enlisted into it via
/// <see cref="IOutboxFlush.FlushAsync"/> become visible together, in one all-or-nothing commit,
/// or not at all if the transaction is disposed without being committed (rollback).
/// </summary>
public interface IEventAppendTransaction : IAsyncDisposable
{
    /// <summary>
    /// Appends <paramref name="events"/> to <paramref name="streamId"/> within this transaction.
    /// The append is buffered until <see cref="CommitAsync"/> publishes it — it MUST NOT become
    /// visible to readers before the commit. This path carries domain events only; integration
    /// events never pass through <see cref="AppendAsync"/> (03-contracts.md §4).
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
    /// <paramref name="expectedVersion"/>'s guard was violated.
    /// </exception>
    ValueTask<AppendResult> AppendAsync(
        string streamId,
        long expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically publishes every append made through this transaction together with any
    /// integration events enlisted into it by <see cref="IOutboxFlush.FlushAsync"/> — all in one
    /// all-or-nothing commit (AK-1). A transaction MUST NOT be committed more than once, and no
    /// further <see cref="AppendAsync"/> call is valid once committed.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask CommitAsync(CancellationToken ct = default);
}
