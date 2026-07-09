using Acta.Abstractions;

using Npgsql;

namespace Acta.Postgres.Store;

/// <summary>
/// PostgreSQL-backed <see cref="IEventAppendTransaction"/> (task 8.4, AK-1/ADR-002/FR-14): the unit
/// of atomicity behind the single-commit outbox seam on the real Postgres backend, wrapping one
/// genuine <see cref="NpgsqlTransaction"/> under READ COMMITTED — the same isolation level
/// <see cref="PostgresEventStore.AppendAsync"/> uses.
/// <para>
/// <b>Append without commit.</b> <see cref="AppendAsync"/> delegates to the same, non-committing
/// <see cref="PostgresAppendCommands.ExecuteAppendAsync"/> executor <see cref="PostgresEventStore"/>
/// uses (single source of truth for the append flow, D1). Every write already lands in the
/// underlying transaction, but transaction isolation — not application-level buffering, unlike the
/// in-memory stand-in — is what keeps it invisible to every other connection until
/// <see cref="CommitAsync"/> runs. <see cref="PostgresOutboxFlush"/> enlists outbox rows into the SAME
/// connection/transaction via the internal <see cref="Connection"/>/<see cref="Transaction"/>/
/// <see cref="Schema"/> members (mirroring how <c>InMemoryOutboxFlush</c> reaches into
/// <c>InMemoryEventAppendTransaction.EnlistOutbox</c>), so <see cref="CommitAsync"/> publishes both
/// atomically.
/// </para>
/// <para>
/// <b>No retry across the seam (D2).</b> Unlike <see cref="PostgresEventStore.AppendAsync"/>, this
/// type never retries a <c>unique_violation</c> (SqlState 23505) from a genuinely-parallel first
/// append to a brand-new stream: such an error aborts the whole underlying PostgreSQL transaction —
/// including any other appends and outbox entries already enlisted into it — so a partial,
/// same-transaction retry is not possible. The exception propagates; the caller's <c>await using</c>
/// disposes (rolls back) this transaction, and re-running the whole unit of work is the caller's
/// responsibility (e.g. a command-handler-level retry).
/// </para>
/// <para>
/// <b>Contract note (PERF-1).</b> Do not perform network I/O or slow work between
/// <see cref="PostgresEventAppendTransactionFactory.BeginAsync"/> and <see cref="CommitAsync"/>
/// beyond the append/outbox SQL itself: the underlying connection holds row locks (the appended-to
/// streams' <c>FOR UPDATE</c> head rows) for the whole open window, and holding them longer than
/// necessary needlessly serializes concurrent appends to those same streams.
/// </para>
/// <para>
/// <b>Concurrency contract.</b> A single <see cref="NpgsqlTransaction"/>/<see cref="NpgsqlConnection"/>
/// is not thread-safe. Unlike the in-memory stand-in (guarded by a <see cref="Lock"/>), this type
/// adds no locking of its own — the contract is sequential use within the handling of one command:
/// no two members of a single instance may be called concurrently.
/// </para>
/// </summary>
public sealed class PostgresEventAppendTransaction : IEventAppendTransaction
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly string _schema;
    private readonly TimeProvider _timeProvider;
    private bool _committed;
    private bool _disposed;

    /// <summary>
    /// Wraps an already-open <paramref name="connection"/>/<paramref name="transaction"/> pair —
    /// normally constructed only by <see cref="PostgresEventAppendTransactionFactory.BeginAsync"/>.
    /// </summary>
    /// <param name="connection">The open connection <paramref name="transaction"/> was begun on; owned by this instance from here on.</param>
    /// <param name="transaction">The database transaction every append/outbox statement enlisted into this instance runs under.</param>
    /// <param name="schema">
    /// The fail-fast-validated schema name (<see cref="Acta.Postgres.Configuration.SchemaName.Validate"/>) —
    /// sourced only from the factory that validated it (SEC-2), never re-validated here.
    /// </param>
    /// <param name="timeProvider">Clock used to stamp <c>created_at</c> on events appended through this transaction.</param>
    public PostgresEventAppendTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _connection = connection;
        _transaction = transaction;
        _schema = schema;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// The connection this transaction runs on. Internal access lets an <see cref="IOutboxFlush"/>
    /// implementation (<see cref="PostgresOutboxFlush"/>) enlist outbox rows into the very same
    /// transaction — the Postgres analogue of <c>InMemoryEventAppendTransaction.EnlistOutbox</c>.
    /// </summary>
    internal NpgsqlConnection Connection => _connection;

    /// <summary>The transaction every append/outbox statement enlisted into this instance runs under.</summary>
    internal NpgsqlTransaction Transaction => _transaction;

    /// <summary>
    /// The fail-fast-validated schema name outbox INSERTs must target (SEC-2: the only source of
    /// the schema name for an enlisted outbox INSERT — never a separately-supplied field on the
    /// flush implementation).
    /// </summary>
    internal string Schema => _schema;

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">This transaction has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">This transaction has already been committed.</exception>
    public async ValueTask<AppendResult> AppendAsync(
        string streamId,
        long expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();

        ThrowIfDisposed();
        ThrowIfCommitted("append to");

        return await PostgresAppendCommands.ExecuteAppendAsync(
            _connection, _transaction, _schema, _timeProvider, streamId, expectedVersion, events, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">This transaction has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">This transaction has already been committed.</exception>
    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        ThrowIfDisposed();
        ThrowIfCommitted("commit");

        await _transaction.CommitAsync(ct).ConfigureAwait(false);
        _committed = true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent — a second call is a no-op. When this transaction was never committed, disposing
    /// <see cref="Transaction"/> rolls it back (Npgsql's documented behavior for disposing an
    /// uncommitted <see cref="NpgsqlTransaction"/>) before the transaction and connection are
    /// themselves disposed — nothing this transaction wrote (appends or outbox rows) becomes visible.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfCommitted(string action)
    {
        if (_committed)
        {
            throw new InvalidOperationException($"Cannot {action} a transaction that has already been committed.");
        }
    }
}
