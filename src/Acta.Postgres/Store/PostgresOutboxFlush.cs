using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Acta.Abstractions;
using Acta.Diagnostics;

using Npgsql;

using NpgsqlTypes;

namespace Acta.Postgres.Store;

/// <summary>
/// PostgreSQL-backed <see cref="IOutboxFlush"/> (task 8.4, AK-1/ADR-002/FR-14, D2 port 2/3): drains
/// <paramref name="collector"/> and inserts the drained integration events into the
/// <c>{schema}.outbox</c> table — already present in the frozen migration
/// <c>0001_initial_schema.sql</c>, no DDL change — within the SAME connection/transaction as
/// <paramref name="tx"/>'s domain-event appends, so both become visible together on
/// <see cref="IEventAppendTransaction.CommitAsync"/> or vanish together on rollback.
/// <para>
/// <b>Backend-specific enlistment.</b> Works only against <see cref="PostgresEventAppendTransaction"/> —
/// the same casting pattern <c>InMemoryOutboxFlush</c> uses against its own in-memory transaction
/// type; a foreign <see cref="IEventAppendTransaction"/> implementation is rejected with
/// <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// <b>No publication (ADR-002).</b> This type never touches the network — it only persists rows;
/// delivering them to a broker is the separate, roadmap-deferred responsibility of an
/// <c>IIntegrationEventPublisher</c> relay (open question OQ-1, task 8.4 plan).
/// </para>
/// <para>
/// <b>Column mapping (04-data, decision D3):</b> <c>message_id</c> = <see cref="EventMetadata.MessageId"/>;
/// <c>event_type</c> = <c>Event.GetType().FullName</c>, defensively coalesced to
/// <c>Event.GetType().Name</c> when <c>FullName</c> is <see langword="null"/> (open generics and
/// array types can report a <see langword="null"/> <c>FullName</c>, and the column is
/// <c>NOT NULL</c>) — a CLR-name placeholder until a logical integration-event type registry lands
/// alongside the relay adapter (open question OQ-3); <c>payload</c>/<c>metadata</c> are serialized via
/// <see cref="System.Text.Json"/> (<see cref="JsonSerializerDefaults.Web"/>) — the same options
/// <see cref="PostgresEventStore"/> uses for <see cref="EventMetadata"/>; <c>tenant_id</c> =
/// <see cref="EventMetadata.TenantId"/> coalesced to <c>""</c> (ADR-016); <c>created_at</c>,
/// <c>published_at</c>, and <c>id</c> are left to their column defaults (<c>now()</c>,
/// <see langword="NULL"/>, IDENTITY).
/// </para>
/// <para>
/// <b>Set-based INSERT (PERF-2).</b> Every drained event is written in one round trip via
/// <c>unnest(...)</c> — the same style <see cref="PostgresAppendCommands"/> uses for events — rather
/// than a per-row loop, minimizing the time the enclosing transaction (and any stream-head
/// <c>FOR UPDATE</c> row locks it holds) stays open.
/// </para>
/// </summary>
/// <param name="collector">The collector drained on every <see cref="FlushAsync"/> call.</param>
/// <param name="logger">
/// Emits the same structured, payload-free "outbox flush enlisted N event(s)" log entry as
/// <c>InMemoryOutboxFlush</c> (task 8.6, decision D-4); <see langword="null"/> (the default)
/// disables logging — additive, null-safe.
/// </param>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The only interpolated identifier is the schema name, sourced exclusively from the " +
        "rejected-if-foreign PostgresEventAppendTransaction passed to FlushAsync — itself only ever " +
        "constructed by PostgresEventAppendTransactionFactory from a value validated fail-fast by " +
        "SchemaName.Validate — never a separately-supplied field on this type (SEC-2). The sanctioned " +
        "identifier-interpolation exception (audit S1, R3, security scan #7; CONSTITUTION §2 " +
        "FORBIDDEN). All non-identifier values (message id, event type, payload, metadata, tenant id) " +
        "travel as NpgsqlParameter.")]
public sealed class PostgresOutboxFlush(IIntegrationEventCollector collector, ILogger<PostgresOutboxFlush>? logger = null)
    : IOutboxFlush
{
    private static readonly JsonSerializerOptions OutboxJsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="tx"/> is not a <see cref="PostgresEventAppendTransaction"/>.
    /// </exception>
    public async ValueTask FlushAsync(IEventAppendTransaction tx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ct.ThrowIfCancellationRequested();

        if (tx is not PostgresEventAppendTransaction postgresTx)
        {
            throw new InvalidOperationException(
                $"PostgresOutboxFlush requires a PostgresEventAppendTransaction, but received {tx.GetType().Name}.");
        }

        using var activity = ActaDiagnostics.ActivitySource.StartActivity(ActaDiagnostics.OutboxFlushSpan, ActivityKind.Internal);

        var drained = collector.Drain();
        if (drained.Count > 0)
        {
            await InsertOutboxRowsAsync(postgresTx, drained, ct).ConfigureAwait(false);
        }

        activity?.SetTag(ActaDiagnostics.EventCountTag, drained.Count);
        logger?.OutboxFlushed(drained.Count);
    }

    private static async Task InsertOutboxRowsAsync(
        PostgresEventAppendTransaction tx, IReadOnlyList<CollectedIntegrationEvent> events, CancellationToken ct)
    {
        var messageIds = new Guid[events.Count];
        var eventTypes = new string[events.Count];
        var payloads = new string[events.Count];
        var metadatas = new string[events.Count];
        var tenantIds = new string[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var (integrationEvent, metadata) = events[i];
            var clrType = integrationEvent.GetType();
            messageIds[i] = metadata.MessageId;
            eventTypes[i] = clrType.FullName ?? clrType.Name;
            payloads[i] = JsonSerializer.Serialize(integrationEvent, OutboxJsonOptions);
            metadatas[i] = JsonSerializer.Serialize(metadata, OutboxJsonOptions);
            tenantIds[i] = metadata.TenantId ?? string.Empty;
        }

        var sql =
            $"""
             INSERT INTO {tx.Schema}.outbox (message_id, event_type, payload, metadata, tenant_id)
             SELECT t.message_id, t.event_type, t.payload::jsonb, t.metadata::jsonb, t.tenant_id
             FROM unnest(@message_ids, @event_types, @payloads, @metadatas, @tenant_ids)
                  AS t(message_id, event_type, payload, metadata, tenant_id)
             """;

        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        command.Parameters.Add(new NpgsqlParameter("message_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = messageIds });
        command.Parameters.Add(new NpgsqlParameter("event_types", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = eventTypes });
        command.Parameters.Add(new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = payloads });
        command.Parameters.Add(new NpgsqlParameter("metadatas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = metadatas });
        command.Parameters.Add(new NpgsqlParameter("tenant_ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = tenantIds });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
