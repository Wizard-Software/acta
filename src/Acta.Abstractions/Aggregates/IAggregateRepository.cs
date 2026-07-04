namespace Acta.Abstractions;

/// <summary>
/// Port for loading and persisting event-sourced aggregate roots (FR-2/FR-11, MODULE-INTERFACES
/// Grupa 3). Read paths fold an aggregate's event stream into a fresh instance (snapshot-first is
/// a reserved seam — no snapshot store exists yet, see <c>AggregateRepository{TAggregate}</c>);
/// the write path applies an explicit optimistic-concurrency guard via
/// <paramref name="expectedVersion"/>, matching the <see cref="ExpectedVersion"/> sentinel matrix
/// used by <see cref="IEventStore.AppendAsync"/>.
/// </summary>
/// <typeparam name="TAggregate">
/// The concrete aggregate type. Requires a public parameterless constructor so an implementation
/// can materialize a fresh instance before folding history into it via
/// <see cref="AggregateRoot.LoadFromHistory"/>.
/// </typeparam>
public interface IAggregateRepository<TAggregate>
    where TAggregate : AggregateRoot, new()
{
    /// <summary>
    /// Loads the aggregate identified by <paramref name="id"/>, folding its full event history
    /// into a fresh <typeparamref name="TAggregate"/> instance.
    /// </summary>
    /// <param name="id">
    /// The aggregate's stream identifier, used verbatim as the store's stream id — a technical,
    /// PII-free identifier (ADR-008); any <c>{category}-{id}</c> naming convention is the
    /// caller's responsibility.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// The hydrated aggregate, or <see langword="null"/> when <paramref name="id"/>'s stream is
    /// empty or does not exist.
    /// </returns>
    ValueTask<TAggregate?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Load-for-write: a strongly-consistent, single-stream read combined with the read version
    /// needed to guard the subsequent write, in one round trip (FR-2). Multi-stream read models
    /// remain eventually consistent — this method is for the single-stream write path only.
    /// <para>
    /// Unlike <see cref="GetByIdAsync"/>, an empty or non-existent stream yields a fresh
    /// <typeparamref name="TAggregate"/> (<see cref="AggregateRoot.Version"/> = -1) rather than
    /// <see langword="null"/>, so create-or-update flows can call this method uniformly.
    /// </para>
    /// </summary>
    /// <param name="id">The aggregate's stream identifier, used verbatim as the store's stream id.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="AggregateWriteSession{TAggregate}"/> carrying the (possibly fresh) aggregate
    /// and the version it was read at.
    /// </returns>
    ValueTask<AggregateWriteSession<TAggregate>> FetchForWritingAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Appends <paramref name="aggregate"/>'s uncommitted events, guarded by
    /// <paramref name="expectedVersion"/>, and clears the uncommitted queue on success — including
    /// an idempotent duplicate (<see cref="AppendResult.Deduplicated"/> = <see langword="true"/>):
    /// the events are already persisted, so the queue is cleared either way.
    /// <para>
    /// The target stream id is <see cref="AggregateRoot.Id"/> — the aggregate carries its own
    /// identity (set by the event that created it), since this method has no separate id
    /// parameter.
    /// </para>
    /// </summary>
    /// <param name="aggregate">
    /// The aggregate whose <see cref="AggregateRoot.UncommittedEvents"/> are to be appended. Its
    /// <see cref="AggregateRoot.Id"/> must be set.
    /// </param>
    /// <param name="expectedVersion">
    /// The optimistic-concurrency guard — typically the <see cref="AggregateWriteSession{TAggregate}.ReadVersion"/>
    /// from a prior <see cref="FetchForWritingAsync"/> call, passed through to the store as-is.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The <see cref="AppendResult"/> describing the outcome of the append.</returns>
    /// <exception cref="ArgumentException"><paramref name="aggregate"/>'s <see cref="AggregateRoot.Id"/> is null or empty.</exception>
    /// <exception cref="ConcurrencyException">
    /// <paramref name="expectedVersion"/>'s guard was violated by a genuinely new (non-duplicate) batch.
    /// </exception>
    ValueTask<AppendResult> SaveAsync(TAggregate aggregate, long expectedVersion, CancellationToken ct = default);
}
