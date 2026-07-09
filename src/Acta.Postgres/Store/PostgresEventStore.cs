using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Acta.Abstractions;
using Acta.Diagnostics;
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
    private const string Backend = "postgres";

    private static readonly JsonSerializerOptions MetadataOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly TimeProvider _timeProvider;
    private readonly EventStoreMetrics? _metrics;
    private readonly ILogger<PostgresEventStore>? _logger;

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
    /// <param name="metrics">
    /// Records <c>acta.append.throughput</c> for every <see cref="AppendAsync"/> call (task 8.6);
    /// <see langword="null"/> (the default) disables the recording — additive, null-safe.
    /// </param>
    /// <param name="logger">
    /// Emits structured, payload-free append/read log entries (task 8.6, decision D-4);
    /// <see langword="null"/> (the default) disables logging — additive, null-safe.
    /// </param>
    /// <exception cref="ArgumentException">The configured schema name is outside the allow-list.</exception>
    public PostgresEventStore(
        NpgsqlDataSource dataSource,
        ActaPostgresOptions options,
        TimeProvider? timeProvider = null,
        EventStoreMetrics? metrics = null,
        ILogger<PostgresEventStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _schema = SchemaName.Validate(options.SchemaName);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics;
        _logger = logger;
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

        using var activity = ActaDiagnostics.ActivitySource.StartActivity(ActaDiagnostics.AppendSpan, ActivityKind.Internal);
        activity?.SetTag(ActaDiagnostics.StreamIdTag, streamId);
        activity?.SetTag(ActaDiagnostics.BackendTag, Backend);

        AppendResult result;
        try
        {
            result = await AppendCoreAsync(streamId, expectedVersion, events, ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // D2: a genuinely-parallel first append raced us between FOR UPDATE (which cannot lock a
            // not-yet-existing streams row) and INSERT. The competitor has now committed, so the
            // stream row exists — re-run once: the retry's FOR UPDATE serializes on that row and the
            // dedup-before-guard path resolves the outcome (Deduplicated / ConcurrencyException /
            // genuine append) with no possibility of a further 23505 for this stream.
            result = await AppendCoreAsync(streamId, expectedVersion, events, ct).ConfigureAwait(false);
        }

        activity?.SetTag(ActaDiagnostics.EventCountTag, events.Count);
        _metrics?.RecordAppend(events.Count, Backend);
        _logger?.AppendCommitted(streamId, events.Count, result.LastGlobalPosition.Value, Backend);

        return result;
    }

    // Task 8.4: the append SQL steps (stream-head FOR UPDATE, empty-batch no-op, dedup-before-guard,
    // concurrency guard, INSERT + head UPDATE) live in the shared, non-committing
    // PostgresAppendCommands executor — the same one PostgresEventAppendTransaction delegates to for
    // the AK-1 single-commit outbox seam (ADR-002, FR-14). This method's only remaining
    // responsibility is opening its own connection/transaction and committing exactly once, after
    // the executor returns, regardless of which branch (no-op / dedup / genuine write) it took.
    private async Task<AppendResult> AppendCoreAsync(
        string streamId,
        long expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var result = await PostgresAppendCommands.ExecuteAppendAsync(
            connection, transaction, _schema, _timeProvider, streamId, expectedVersion, events, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
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
        // GAP-6: the Activity is started HERE, inside the async-iterator body, so the span covers the
        // whole enumeration. No per-event SetTag in the loop below — only once, after it completes.
        using var activity = ActaDiagnostics.ActivitySource.StartActivity(ActaDiagnostics.ReadSpan, ActivityKind.Internal);
        activity?.SetTag(ActaDiagnostics.StreamIdTag, streamId);
        activity?.SetTag(ActaDiagnostics.BackendTag, Backend);

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

        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            count++;
            yield return MapEvent(reader);
        }

        activity?.SetTag(ActaDiagnostics.EventCountTag, count);
        _logger?.ReadCompleted(count, Backend, streamId);
    }

    private async IAsyncEnumerable<StoredEvent> ReadAllCoreAsync(
        GlobalPosition from,
        GlobalPosition? upTo,
        int? maxCount,
        Direction direction,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // GAP-6: started inside this async-iterator body so the span covers the whole enumeration.
        using var activity = ActaDiagnostics.ActivitySource.StartActivity(ActaDiagnostics.ReadSpan, ActivityKind.Internal);
        activity?.SetTag(ActaDiagnostics.BackendTag, Backend);

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

        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            count++;
            yield return MapEvent(reader);
        }

        activity?.SetTag(ActaDiagnostics.EventCountTag, count);
        _logger?.ReadCompleted(count, Backend, streamId: null);
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

    // SEC-1: emit the sort direction as a constant literal chosen by a switch over the closed
    // Direction enum — never direction.ToString() — so the interpolated token can only ever be one
    // of two compile-time constants.
    private static string OrderToken(Direction direction) =>
        direction == Direction.Backwards ? "DESC" : "ASC";
}
