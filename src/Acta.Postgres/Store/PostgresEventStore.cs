using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Acta.Abstractions;
using Acta.Postgres.Configuration;

using Npgsql;

using NpgsqlTypes;

namespace Acta.Postgres.Store;

/// <summary>
/// PostgreSQL-backed <see cref="IEventStore"/> (ADR-004 — pure Npgsql, no ORM): the Tier-2 backend
/// used by <c>AddActaPostgres(...)</c> for any topology with more than one pod (ADR-014). It
/// implements the exact same append/read contract as <c>InMemoryEventStore</c> and is verified by
/// the shared <c>EventStoreContractTests</c> suite running against real PostgreSQL (AK-3 parity,
/// task 7.2).
/// <para>
/// <b>Transactional append (04-data §3.1):</b> every <see cref="AppendAsync"/> runs in one
/// transaction under READ COMMITTED. A <c>SELECT … FOR UPDATE</c> on the stream head row serializes
/// concurrent appends to an <i>existing</i> stream; deduplication on <c>(stream_id, event_id)</c> is
/// evaluated <i>before</i> the concurrency guard (ADR-003, D3), so a replay of an already-applied
/// command is always an idempotent success — it never throws, even when the guard would otherwise
/// have failed. Dedup uses an explicit <c>SELECT</c> (not <c>ON CONFLICT DO NOTHING</c>): a conflict
/// under <c>ON CONFLICT</c> still consumes the <c>global_position</c> IDENTITY value, opening gaps
/// the contract's contiguous-position reads forbid, and per-row <c>ON CONFLICT</c> would break the
/// whole-batch dedup parity with the in-memory backend (GAP-1).
/// </para>
/// <para>
/// <b>Multi-pod first-append race (D2, ADR-003):</b> <c>SELECT … FOR UPDATE</c> cannot lock a
/// not-yet-existing <c>streams</c> row, so two genuinely-parallel first appends to a brand-new
/// stream can both pass the dedup/guard checks and collide at INSERT time with a
/// <c>unique_violation</c> (SqlState 23505). <see cref="AppendAsync"/> catches that and retries the
/// operation once: after any competitor commits, the <c>streams</c> row exists, the retry's
/// <c>FOR UPDATE</c> acquires its row lock, and the normal dedup-before-guard path resolves
/// deterministically — a duplicate <c>event_id</c> becomes an idempotent
/// <see cref="AppendResult.Deduplicated"/> success and a genuine version clash becomes a
/// <see cref="ConcurrencyException"/>.
/// </para>
/// <para>
/// <b>Reads (ADR-015):</b> <see cref="ReadAllAsync"/> applies no visibility-lag cutback in this
/// port — READ COMMITTED + MVCC guarantees only committed rows are visible, and a freshly-committed
/// event is immediately readable. The high-water-mark cutback for concurrent in-flight sequence
/// values belongs to <c>ISubscriptionSource</c> (task 7.3), not to the event store.
/// </para>
/// <para>
/// <b>Payload vs metadata:</b> <see cref="EventData.Payload"/> is already a serialized JSON document
/// (FR-10) — it is persisted verbatim into the <c>payload</c> <c>jsonb</c> column and read back as
/// UTF-8 bytes, without CLR (de)serialization (jsonb may normalize insignificant whitespace, so raw
/// byte identity is not preserved — semantic JSON identity is; GAP-3). <see cref="EventMetadata"/>
/// is a structural type the store itself (de)serializes to/from the private <c>metadata</c> jsonb
/// column via <see cref="System.Text.Json"/> (<see cref="JsonSerializerDefaults.Web"/>).
/// </para>
/// </summary>
/// <remarks>
/// Only the fail-fast-validated schema name (<see cref="SchemaName.Validate"/>) and the constant
/// <c>ASC</c>/<c>DESC</c> literal emitted from the closed <see cref="Direction"/> enum are ever
/// interpolated into command text; every runtime value (stream id, category, tenant id, versions,
/// event ids, payloads, metadata, range/limit filters) travels as an <see cref="NpgsqlParameter"/>.
/// </remarks>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The only interpolated identifier is the schema name, validated fail-fast against " +
        "^[a-z_][a-z0-9_]{0,62}$ by SchemaName.Validate in the constructor before any use — the " +
        "sanctioned identifier-interpolation exception (audit S1, R3, security scan #7; " +
        "CONSTITUTION §2 FORBIDDEN). The only other interpolated token is the constant ASC/DESC " +
        "literal chosen by a switch over the closed Direction enum (never Direction.ToString()). " +
        "All non-identifier values travel as NpgsqlParameter.")]
public sealed class PostgresEventStore : IEventStore
{
    private const string UqStreamVersion = "uq_events_stream_version";
    private const string UqStreamEventId = "uq_events_stream_eventid";

    private static readonly JsonSerializerOptions MetadataOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a store over <paramref name="dataSource"/>, persisting into
    /// <paramref name="options"/>.<see cref="ActaPostgresOptions.SchemaName"/> (validated fail-fast
    /// here, exactly like <c>MigrationRunner</c>).
    /// </summary>
    /// <param name="dataSource">The Npgsql data source the store opens connections from.</param>
    /// <param name="options">Backend options; the schema name is validated fail-fast in this constructor.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp <c>created_at</c> on appended events, injectable for deterministic tests.
    /// <see langword="null"/> (the default) resolves to <see cref="TimeProvider.System"/>.
    /// </param>
    /// <exception cref="ArgumentException">The configured schema name is outside the allow-list.</exception>
    public PostgresEventStore(NpgsqlDataSource dataSource, ActaPostgresOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _schema = SchemaName.Validate(options.SchemaName);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async ValueTask<AppendResult> AppendAsync(
        string streamId,
        long expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();

        try
        {
            return await AppendCoreAsync(streamId, expectedVersion, events, ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // D2: a genuinely-parallel first append raced us between FOR UPDATE (which cannot lock a
            // not-yet-existing streams row) and INSERT. The competitor has now committed, so the
            // stream row exists — re-run once: the retry's FOR UPDATE serializes on that row and the
            // dedup-before-guard path resolves the outcome (Deduplicated / ConcurrencyException /
            // genuine append) with no possibility of a further 23505 for this stream.
            return await AppendCoreAsync(streamId, expectedVersion, events, ct).ConfigureAwait(false);
        }
    }

    private async Task<AppendResult> AppendCoreAsync(
        string streamId,
        long expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 1) Read + row-lock the stream head. FOR UPDATE serializes appends to an existing stream;
        //    a missing row means the stream does not exist yet (currentLastVersion = -1).
        var (currentLastVersion, rowExists) = await ReadStreamHeadAsync(connection, transaction, streamId, ct)
            .ConfigureAwait(false);

        // 2) Empty batch = idempotent no-op (parity: InMemory OQ #3) — reports the current head.
        if (events.Count == 0)
        {
            var noopHead = await ReadHeadPositionAsync(connection, transaction, streamId, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
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
        if (await BatchHasKnownEventAsync(connection, transaction, streamId, eventIds, ct).ConfigureAwait(false))
        {
            var dedupHead = await ReadHeadPositionAsync(connection, transaction, streamId, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new AppendResult(currentLastVersion, dedupHead, Deduplicated: true);
        }

        // 4) Concurrency guard (throws ConcurrencyException -> await using rolls the transaction back).
        ValidateExpectedVersion(streamId, expectedVersion, currentLastVersion);

        // 5) Write. Create the stream row on first append, then set-based INSERT + head UPDATE.
        var tenantId = events[0].Metadata.TenantId;
        if (!rowExists)
        {
            await InsertStreamRowAsync(connection, transaction, streamId, tenantId, ct).ConfigureAwait(false);
        }

        var createdAt = _timeProvider.GetUtcNow();
        var lastGlobalPosition = await InsertEventsAsync(
            connection, transaction, streamId, tenantId, createdAt,
            versions, eventIds, eventTypes, schemaVersions, payloads, metadatas, ct).ConfigureAwait(false);

        var newLast = currentLastVersion + events.Count;
        await UpdateStreamVersionAsync(connection, transaction, streamId, newLast, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new AppendResult(newLast, new GlobalPosition(lastGlobalPosition), Deduplicated: false);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StoredEvent> ReadStreamAsync(
        string streamId,
        long fromVersion = 0,
        long? toVersion = null,
        Direction direction = Direction.Forwards,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);

        return ReadStreamCoreAsync(streamId, fromVersion, toVersion, direction, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StoredEvent> ReadAllAsync(
        GlobalPosition from,
        GlobalPosition? upTo = null,
        int? maxCount = null,
        Direction direction = Direction.Forwards,
        CancellationToken ct = default)
        => ReadAllCoreAsync(from, upTo, maxCount, direction, ct);

    private async IAsyncEnumerable<StoredEvent> ReadStreamCoreAsync(
        string streamId,
        long fromVersion,
        long? toVersion,
        Direction direction,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sql =
            $"""
             SELECT event_id, stream_id, version, global_position, event_type, schema_version, payload, metadata, created_at
             FROM {_schema}.events
             WHERE stream_id = @sid AND version >= @from AND (@to IS NULL OR version <= @to)
             ORDER BY version {OrderToken(direction)}
             """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sid", streamId);
        command.Parameters.AddWithValue("from", fromVersion);
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Bigint) { Value = (object?)toVersion ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return MapEvent(reader);
        }
    }

    private async IAsyncEnumerable<StoredEvent> ReadAllCoreAsync(
        GlobalPosition from,
        GlobalPosition? upTo,
        int? maxCount,
        Direction direction,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var limitClause = maxCount is null ? string.Empty : "\nLIMIT @maxCount";
        var sql =
            $"""
             SELECT event_id, stream_id, version, global_position, event_type, schema_version, payload, metadata, created_at
             FROM {_schema}.events
             WHERE global_position > @from AND (@upTo IS NULL OR global_position <= @upTo)
             ORDER BY global_position {OrderToken(direction)}{limitClause}
             """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("from", from.Value);
        command.Parameters.Add(new NpgsqlParameter("upTo", NpgsqlDbType.Bigint) { Value = (object?)upTo?.Value ?? DBNull.Value });
        if (maxCount is { } limit)
        {
            command.Parameters.AddWithValue("maxCount", limit);
        }

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return MapEvent(reader);
        }
    }

    private async Task<(long CurrentLastVersion, bool RowExists)> ReadStreamHeadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string streamId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT current_version FROM {_schema}.streams WHERE stream_id = @sid FOR UPDATE",
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
    private async Task<GlobalPosition> ReadHeadPositionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string streamId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT global_position FROM {_schema}.events WHERE stream_id = @sid ORDER BY version DESC LIMIT 1",
            connection, transaction);
        command.Parameters.AddWithValue("sid", streamId);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long position ? new GlobalPosition(position) : GlobalPosition.Start;
    }

    private async Task<bool> BatchHasKnownEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string streamId, Guid[] eventIds, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {_schema}.events WHERE stream_id = @sid AND event_id = ANY(@ids)",
            connection, transaction);
        command.Parameters.AddWithValue("sid", streamId);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = eventIds });

        var count = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return count > 0;
    }

    private async Task InsertStreamRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string streamId, string? tenantId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {_schema}.streams (stream_id, category, tenant_id, current_version) VALUES (@sid, @category, @tenant, -1)",
            connection, transaction);
        command.Parameters.AddWithValue("sid", streamId);
        command.Parameters.AddWithValue("category", DeriveCategory(streamId));
        command.Parameters.AddWithValue("tenant", (object?)tenantId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<long> InsertEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
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
             INSERT INTO {_schema}.events
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

    private async Task UpdateStreamVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string streamId, long newLast, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {_schema}.streams SET current_version = @version WHERE stream_id = @sid",
            connection, transaction);
        command.Parameters.AddWithValue("version", newLast);
        command.Parameters.AddWithValue("sid", streamId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static StoredEvent MapEvent(NpgsqlDataReader reader) =>
        new(
            reader.GetFieldValue<Guid>(0),
            reader.GetString(1),
            reader.GetInt64(2),
            new GlobalPosition(reader.GetInt64(3)),
            reader.GetString(4),
            reader.GetInt32(5),
            Encoding.UTF8.GetBytes(reader.GetFieldValue<string>(6)),
            JsonSerializer.Deserialize<EventMetadata>(reader.GetFieldValue<string>(7), MetadataOptions)!,
            reader.GetFieldValue<DateTimeOffset>(8));

    // Convention {category}-{id} (CONSTITUTION §1.1, D8 no PII): the prefix before the first '-',
    // or the whole streamId when it has no '-'.
    private static string DeriveCategory(string streamId)
    {
        var dashIndex = streamId.IndexOf('-', StringComparison.Ordinal);
        return dashIndex >= 0 ? streamId[..dashIndex] : streamId;
    }

    // SEC-1: emit the sort direction as a constant literal chosen by a switch over the closed
    // Direction enum — never direction.ToString() — so the interpolated token can only ever be one
    // of two compile-time constants.
    private static string OrderToken(Direction direction) =>
        direction == Direction.Backwards ? "DESC" : "ASC";

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
