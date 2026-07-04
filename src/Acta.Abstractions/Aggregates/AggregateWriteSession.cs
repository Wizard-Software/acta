namespace Acta.Abstractions;

/// <summary>
/// A load-for-write session: holds the aggregate returned by
/// <see cref="IAggregateRepository{TAggregate}.FetchForWritingAsync"/> together with the version
/// it was read at, and lets the caller save it back through the repository that created it
/// (FR-2).
/// <para>
/// <see cref="ReadVersion"/> mirrors <see cref="AggregateRoot.Version"/> at load time and is
/// passed through, unmodified, as the optimistic-concurrency guard on <see cref="SaveAsync"/>:
/// <c>-1</c> (a fresh aggregate) coincides with <see cref="ExpectedVersion.NoStream"/> by design,
/// so no conditional mapping is needed between the aggregate's version and the store's guard.
/// </para>
/// </summary>
/// <typeparam name="TAggregate">The concrete aggregate type held by this session.</typeparam>
/// <remarks>
/// The <c>new()</c> constraint below is required only because this session holds a private
/// <see cref="IAggregateRepository{TAggregate}"/> reference — that port itself constrains
/// <typeparamref name="TAggregate"/> with <c>new()</c> (so implementations can materialize a
/// fresh instance before folding history into it). It does not change this type's public surface:
/// every <see cref="AggregateWriteSession{TAggregate}"/> in practice is already produced by
/// <see cref="IAggregateRepository{TAggregate}.FetchForWritingAsync"/>, whose own <c>TAggregate</c>
/// already satisfies <c>new()</c>.
/// </remarks>
public sealed class AggregateWriteSession<TAggregate>
    where TAggregate : AggregateRoot, new()
{
    private readonly IAggregateRepository<TAggregate> _repository;

    /// <summary>The aggregate as read by the originating <c>FetchForWritingAsync</c> call.</summary>
    public TAggregate Aggregate { get; }

    /// <summary>
    /// <see cref="Aggregate"/>'s <see cref="AggregateRoot.Version"/> at the moment it was read —
    /// the optimistic-concurrency guard used by <see cref="SaveAsync"/>.
    /// </summary>
    public long ReadVersion { get; }

    /// <summary>
    /// Creates a session wrapping an already-loaded <paramref name="aggregate"/> and the
    /// repository it should be saved back through.
    /// <para>
    /// This constructor is <see langword="public"/> by design (plan 3.2 §9, Q3): it is normally
    /// called only by <see cref="IAggregateRepository{TAggregate}"/> implementations (in the core
    /// assembly), but keeping it public avoids granting <c>InternalsVisibleTo</c> across the
    /// assembly boundary for a single constructor call.
    /// </para>
    /// </summary>
    /// <param name="aggregate">The aggregate to hold, as read at <paramref name="readVersion"/>.</param>
    /// <param name="readVersion">The version <paramref name="aggregate"/> was read at.</param>
    /// <param name="repository">The repository <see cref="SaveAsync"/> delegates to.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="aggregate"/> or <paramref name="repository"/> is <see langword="null"/>.
    /// </exception>
    public AggregateWriteSession(TAggregate aggregate, long readVersion, IAggregateRepository<TAggregate> repository)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(repository);

        Aggregate = aggregate;
        ReadVersion = readVersion;
        _repository = repository;
    }

    /// <summary>
    /// Saves <see cref="Aggregate"/>'s uncommitted events back through the originating repository,
    /// guarded by <see cref="ReadVersion"/>. Delegates to
    /// <see cref="IAggregateRepository{TAggregate}.SaveAsync"/> — this type carries no store
    /// dependency of its own.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The <see cref="AppendResult"/> describing the outcome of the append.</returns>
    /// <exception cref="ConcurrencyException">
    /// <see cref="ReadVersion"/>'s guard was violated by a genuinely new (non-duplicate) batch —
    /// another writer appended to the stream since this session was fetched.
    /// </exception>
    public ValueTask<AppendResult> SaveAsync(CancellationToken ct = default)
        => _repository.SaveAsync(Aggregate, ReadVersion, ct);
}
