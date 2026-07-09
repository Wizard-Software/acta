using Acta.Abstractions;
using Acta.Postgres.Configuration;

using Npgsql;

namespace Acta.Postgres.Store;

/// <summary>
/// PostgreSQL-backed <see cref="IEventAppendTransactionFactory"/> (task 8.4, AK-1/ADR-002/FR-14):
/// begins a genuine <see cref="NpgsqlTransaction"/> under READ COMMITTED — the same isolation level
/// <see cref="PostgresEventStore.AppendAsync"/> uses — and wraps it in a
/// <see cref="PostgresEventAppendTransaction"/>.
/// </summary>
/// <param name="dataSource">The Npgsql data source every transaction begun by this factory opens its connection from.</param>
/// <param name="options">Backend options; the schema name is validated fail-fast in this constructor (parity <see cref="PostgresEventStore"/>).</param>
/// <param name="timeProvider">
/// Clock used to stamp <c>created_at</c> on every event appended through a transaction this factory
/// begins, injectable for deterministic tests. <see langword="null"/> (the default) resolves to
/// <see cref="TimeProvider.System"/>.
/// </param>
/// <exception cref="ArgumentException">The configured schema name is outside the allow-list.</exception>
public sealed class PostgresEventAppendTransactionFactory(
    NpgsqlDataSource dataSource,
    ActaPostgresOptions options,
    TimeProvider? timeProvider = null) : IEventAppendTransactionFactory
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private readonly string _schema =
        SchemaName.Validate((options ?? throw new ArgumentNullException(nameof(options))).SchemaName);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public async ValueTask<IEventAppendTransaction> BeginAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            return new PostgresEventAppendTransaction(connection, transaction, _schema, _timeProvider);
        }
        catch
        {
            // BeginTransactionAsync (or an earlier cancellation) failed after the connection was
            // already opened — dispose it here so a failed BeginAsync never leaks a pooled connection.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
