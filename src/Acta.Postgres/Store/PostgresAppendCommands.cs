using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

using Acta.Abstractions;

using Npgsql;

using NpgsqlTypes;

namespace Acta.Postgres.Store;

/// <summary>
/// Shared, <b>non-committing</b> executor for the append SQL steps (04-data §3.1, task 8.4) — the
/// single source of truth for the append flow used by both <see cref="PostgresEventStore"/> (its own
/// connection + transaction, committed immediately after this method returns) and
/// <see cref="PostgresEventAppendTransaction"/> (a caller-owned connection + transaction, committed
/// only when its own <c>CommitAsync</c> is called — the AK-1 single-commit outbox seam, ADR-002,
/// FR-14). Extracted from the original, monolithic <c>PostgresEventStore.AppendCoreAsync</c> so the
/// two callers can never drift apart on dedup/guard/write behavior (coverage matrix parity, 04-data
/// §4).
/// <para>
/// <b>Never commits (GAP-1).</b> Every branch below — the empty-batch no-op, the whole-batch dedup,
/// and the genuine write — returns an <see cref="AppendResult"/> without ever calling
/// <see cref="NpgsqlTransaction.CommitAsync(System.Threading.CancellationToken)"/>. Exactly one
/// commit happens, once, in the caller.
/// </para>
/// <para>
/// <b>Steps (parity with the pre-8.4 <c>PostgresEventStore.AppendCoreAsync</c>):</b> (1) read + row-
/// lock the stream head via <c>SELECT … FOR UPDATE</c>; (2) an empty batch is an idempotent no-op
/// reporting the current head; (3) deduplicate the whole batch <i>before</i> the concurrency guard
/// (ADR-003, D3) via an explicit <c>SELECT</c> — never <c>ON CONFLICT</c>, which would both consume a
/// <c>global_position</c> IDENTITY value on a duplicate (opening gaps the contiguous-position read
/// contract forbids) and break whole-batch dedup parity with the in-memory backend; (4) validate the
/// optimistic-concurrency guard; (5) create the stream row on first append, then set-based
/// <c>INSERT</c> the events and update the stream head.
/// </para>
/// </summary>
/// <remarks>
/// Only the fail-fast-validated schema name is ever interpolated into command text — validated by
/// <see cref="Acta.Postgres.Configuration.SchemaName.Validate"/> in either caller's constructor
/// before this executor is ever reached. Every runtime value (stream id, category, tenant id,
/// versions, event ids, payloads, metadata) travels as an <see cref="NpgsqlParameter"/>.
/// </remarks>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The only interpolated identifier is the schema name, validated fail-fast against " +
        "^[a-z_][a-z0-9_]{0,62}$ by SchemaName.Validate in the caller's constructor " +
        "(PostgresEventStore or PostgresEventAppendTransactionFactory) before this executor is ever " +
        "reached — the sanctioned identifier-interpolation exception (audit S1, R3, security scan #7; " +
        "CONSTITUTION §2 FORBIDDEN). All non-identifier values travel as NpgsqlParameter.")]
internal static class PostgresAppendCommands
{
    private static readonly JsonSerializerOptions MetadataOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Executes the full append flow against <paramref name="connection"/>/<paramref name="transaction"/>
    /// without committing — see the type-level remarks for the commit contract and step breakdown.
    /// </summary>
    /// <param name="connection">An open connection bound to <paramref name="transaction"/>.</param>
    /// <param name="transaction">The transaction every statement in this call runs under.</param>
    /// <param name="schema">
    /// The fail-fast-validated schema name (<see cref="Acta.Postgres.Configuration.SchemaName.Validate"/>).
    /// </param>
    /// <param name="timeProvider">Clock used to stamp <c>created_at</c> on newly-appended events.</param>
    /// <param name="streamId">Identifier of the target stream.</param>
    /// <param name="expectedVersion">
    /// The optimistic-concurrency guard — one of the <see cref="ExpectedVersion"/> sentinels, or an
    /// exact expected version <c>&gt;= 1</c>.
    /// </param>
    /// <param name="events">The events to append, in order.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// The <see cref="AppendResult"/> describing the outcome — committing the transaction is the
    /// caller's responsibility, not this method's.
    /// </returns>
    /// <exception cref="ConcurrencyException"><paramref name="expectedVersion"/>'s guard was violated.</exception>
    internal static async Task<AppendResult> ExecuteAppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        TimeProvider timeProvider,
        string streamId,
        long expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct)
    {
        // 1) Read + row-lock the stream head. FOR UPDATE serializes appends to an existing stream;
        //    a missing row means the stream does not exist yet (currentLastVersion = -1).
        var (currentLastVersion, rowExists) = await ReadStreamHeadAsync(connection, transaction, schema, streamId, ct)
            .ConfigureAwait(false);

        // 2) Empty batch = idempotent no-op (parity: InMemory OQ #3) — reports the current head.
        //    No commit here (GAP-1) — the caller commits exactly once, after this method returns.
        if (events.Count == 0)
        {
            var noopHead = await ReadHeadPositionAsync(connection, transaction, schema, streamId, ct).ConfigureAwait(false);
            return new AppendResult(currentLastVersion, noopHead, Deduplicated: false);
        }

        var eventIds = new Guid[events.Count];
        var versions = new long[events.Count];
        var eventTypes = new string[events.Count];
        var schemaVersions = new int[events.Count];
        var payloads = new string[events.Count];
        var metadatas = new string[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var eventData = events[i];
            eventIds[i] = eventData.EventId;
            versions[i] = currentLastVersion + 1 + i;
            eventTypes[i] = eventData.EventType;
            schemaVersions[i] = eventData.SchemaVersion;
            payloads[i] = Encoding.UTF8.GetString(eventData.Payload.Span);
            metadatas[i] = JsonSerializer.Serialize(eventData.Metadata, MetadataOptions);
        }

        // 3) Dedup BEFORE the guard (parity: HasAnyDuplicateKey — any already-seen key dedups the
        //    whole batch). Explicit SELECT so the dedup branch consumes no global_position IDENTITY.
        //    No commit here (GAP-1) — same reason as the no-op branch above.
        if (await BatchHasKnownEventAsync(connection, transaction, schema, streamId, eventIds, ct).ConfigureAwait(false))
        {
            var dedupHead = await ReadHeadPositionAsync(connection, transaction, schema, streamId, ct).ConfigureAwait(false);
            return new AppendResult(currentLastVersion, dedupHead, Deduplicated: true);
        }

        // 4) Concurrency guard (throws ConcurrencyException -> caller's `await using` rolls the transaction back).
        ValidateExpectedVersion(streamId, expectedVersion, currentLastVersion);

        // 5) Write. Create the stream row on first append, then set-based INSERT + head UPDATE.
        var tenantId = events[0].Metadata.TenantId;
        if (!rowExists)
        {
            await InsertStreamRowAsync(connection, transaction, schema, streamId, tenantId, ct).ConfigureAwait(false);
        }

        var createdAt = timeProvider.GetUtcNow();
        var lastGlobalPosition = await InsertEventsAsync(
            connection, transaction, schema, streamId, tenantId, createdAt,
            versions, eventIds, eventTypes, schemaVersions, payloads, metadatas, ct).ConfigureAwait(false);

        var newLast = currentLastVersion + events.Count;
        await UpdateStreamVersionAsync(connection, transaction, schema, streamId, newLast, ct).ConfigureAwait(false);

        return new AppendResult(newLast, new GlobalPosition(lastGlobalPosition), Deduplicated: false);
    }

    private static async Task<(long CurrentLastVersion, bool RowExists)> ReadStreamHeadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string streamId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT current_version FROM {schema}.streams WHERE stream_id = @sid FOR UPDATE",
            connection, transaction);
        command.Parameters.AddWithValue("sid", streamId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return (reader.GetInt64(0), true);
        }

        return (-1L, false);
    }

    // D1 (PERF-1): O(1) backward index probe on uq_events_stream_version — only ever run on the
    // no-op / dedup branches that need the current head; the write path takes its head from RETURNING.
    private static async Task<GlobalPosition> ReadHeadPositionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string streamId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT global_position FROM {schema}.events WHERE stream_id = @sid ORDER BY version DESC LIMIT 1",
            connection, transaction);
        command.Parameters.AddWithValue("sid", streamId);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long position ? new GlobalPosition(position) : GlobalPosition.Start;
    }

    private static async Task<bool> BatchHasKnownEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string streamId, Guid[] eventIds, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {schema}.events WHERE stream_id = @sid AND event_id = ANY(@ids)",
            connection, transaction);
        command.Parameters.AddWithValue("sid", streamId);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = eventIds });

        var count = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return count > 0;
    }

    private static async Task InsertStreamRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string streamId, string? tenantId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {schema}.streams (stream_id, category, tenant_id, current_version) VALUES (@sid, @category, @tenant, -1)",
            connection, transaction);
        command.Parameters.AddWithValue("sid", streamId);
        command.Parameters.AddWithValue("category", DeriveCategory(streamId));
        command.Parameters.AddWithValue("tenant", (object?)tenantId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> InsertEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string streamId,
        string? tenantId,
        DateTimeOffset createdAt,
        long[] versions,
        Guid[] eventIds,
        string[] eventTypes,
        int[] schemaVersions,
        string[] payloads,
        string[] metadatas,
        CancellationToken ct)
    {
        var sql =
            $"""
             INSERT INTO {schema}.events
                 (stream_id, version, event_id, event_type, schema_version, payload, metadata, tenant_id, created_at)
             SELECT @sid, t.version, t.event_id, t.event_type, t.schema_version, t.payload::jsonb, t.metadata::jsonb, @tenant, @created_at
             FROM unnest(@versions, @event_ids, @event_types, @schema_versions, @payloads, @metadatas)
                  AS t(version, event_id, event_type, schema_version, payload, metadata)
             RETURNING global_position
             """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("sid", streamId);
        command.Parameters.AddWithValue("tenant", (object?)tenantId ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", createdAt);
        command.Parameters.Add(new NpgsqlParameter("versions", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = versions });
        command.Parameters.Add(new NpgsqlParameter("event_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = eventIds });
        command.Parameters.Add(new NpgsqlParameter("event_types", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = eventTypes });
        command.Parameters.Add(new NpgsqlParameter("schema_versions", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = schemaVersions });
        command.Parameters.Add(new NpgsqlParameter("payloads", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = payloads });
        command.Parameters.Add(new NpgsqlParameter("metadatas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = metadatas });

        var lastGlobalPosition = long.MinValue;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var position = reader.GetInt64(0);
            if (position > lastGlobalPosition)
            {
                lastGlobalPosition = position;
            }
        }

        return lastGlobalPosition;
    }

    private static async Task UpdateStreamVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string streamId, long newLast, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {schema}.streams SET current_version = @version WHERE stream_id = @sid",
            connection, transaction);
        command.Parameters.AddWithValue("version", newLast);
        command.Parameters.AddWithValue("sid", streamId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Convention {category}-{id} (CONSTITUTION §1.1, D8 no PII): the prefix before the first '-',
    // or the whole streamId when it has no '-'.
    private static string DeriveCategory(string streamId)
    {
        var dashIndex = streamId.IndexOf('-', StringComparison.Ordinal);
        return dashIndex >= 0 ? streamId[..dashIndex] : streamId;
    }

    // Exact parity with InMemoryEventStore.ValidateExpectedVersion (03-contracts.md §1, ADR-003).
    // GAP-2: EmptyStream collapses onto NoStream — an existing-but-empty stream is unreachable
    // through the public API on Postgres too (the only path that creates a streams row is an append
    // that ends with current_version >= 0).
    private static void ValidateExpectedVersion(string streamId, long expectedVersion, long currentLastVersion)
    {
        var streamExists = currentLastVersion >= 0;

        var guardSatisfied = expectedVersion switch
        {
            ExpectedVersion.Any => true,
            ExpectedVersion.NoStream => !streamExists,
            ExpectedVersion.StreamExists => streamExists,
            ExpectedVersion.EmptyStream => !streamExists,
            _ => currentLastVersion == expectedVersion,
        };

        if (!guardSatisfied)
        {
            throw new ConcurrencyException(streamId, expectedVersion, currentLastVersion);
        }
    }
}
