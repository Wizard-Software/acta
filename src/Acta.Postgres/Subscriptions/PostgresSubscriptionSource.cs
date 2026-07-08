using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Acta.Abstractions;
using Acta.Postgres.Configuration;

using Npgsql;

using NpgsqlTypes;

namespace Acta.Postgres.Subscriptions;

/// <summary>
/// PostgreSQL-backed <see cref="ISubscriptionSource"/> (ADR-015 — the canonical catch-up read path a
/// projection daemon consumes the all-stream through). The Tier-2 counterpart of
/// <c>InMemorySubscriptionSource</c>, verified by the shared <c>SubscriptionSourceContractTests</c>
/// suite running against real PostgreSQL (AK-3 parity, task 7.3).
/// <para>
/// <b>Safe high-water-mark guard (04-data §3.2, task 7.3 P1):</b> on PostgreSQL
/// <c>global_position</c> is a <c>GENERATED ALWAYS AS IDENTITY</c> sequence whose values are assigned
/// <i>before</i> commit, so a transaction T2 can be granted position <c>N+1</c> and commit while T1
/// still holds position <c>N</c> uncommitted (out-of-order visibility). A naive
/// "everything above the checkpoint" read would return <c>N+1</c>, skip the still-in-flight <c>N</c>,
/// and advance the checkpoint past it — losing <c>N</c> when T1 finally commits (NFR-4 at-least-once
/// violation). <see cref="ReadBatchAsync"/> guards against this with a <b>time-based visibility-lag
/// cutback</b>: it returns only events older than <c>now - VisibilityLag</c>. A gap fresher than the
/// cutback may still fill, so the batch stops before it (safety); a gap older than the cutback is
/// permanent by definition — no in-flight transaction lives longer than <c>VisibilityLag</c> — so it
/// is skipped immediately (liveness). This is the P1 verdict: the load-bearing guard is the
/// <c>VisibilityLag</c> cutback, <b>not</b> a sequence <c>last_value</c> read
/// (<c>pg_sequence_last_value</c> reflects uncommitted allocations and would re-introduce the loss),
/// nor an explicit <c>max(global_position)</c> ceiling (redundant given the cutback + ordering). It
/// is proven by the concurrent-gap-at-end-of-batch test on Testcontainers.
/// </para>
/// <para>
/// <b>Clock authority:</b> the cutback threshold is computed app-side from an injected
/// <see cref="TimeProvider"/> (<c>now - VisibilityLag</c>) rather than the database <c>now()</c>,
/// because <c>PostgresEventStore</c> stamps <c>created_at</c> from the same <see cref="TimeProvider"/>
/// (overriding the DDL <c>now()</c> default). Using one clock authority for both the stamp and the
/// cutback keeps the comparison self-consistent (no app↔DB skew) and deterministically testable with
/// a fake clock.
/// </para>
/// <para>
/// <b>Event-type filter pushdown (ADR-015):</b> <see cref="ReadBatchAsync"/> pushes the type filter
/// down to the backend (<c>WHERE event_type = ANY(@types)</c>) with the <c>LIMIT</c> applied
/// <i>after</i> the filter, so <c>maxCount</c> counts matching events rather than raw events scanned.
/// Filtering on the plain <c>event_type</c> string (no payload deserialization) is permitted for a
/// backend source — the ADR-015 ban on materialized-event type filtering targets the daemon, not the
/// source. <see cref="ReadFromAsync"/> is the live, unbounded path: no type filter and no cutback,
/// in parity with the in-memory source and the port contract.
/// </para>
/// <para>
/// <b>Tenant scope:</b> both reads are over the raw all-stream with no tenant predicate — this is not
/// a regression. <c>PostgresEventStore</c> is itself tenant-agnostic and the projection daemon is
/// single-tenant in the MVP (ADR-014); per-tenant checkpointing is the daemon's concern, layered on
/// top of this source, not the source's.
/// </para>
/// </summary>
/// <remarks>
/// The only value ever interpolated into command text is the fail-fast-validated schema name
/// (<see cref="SchemaName.Validate"/>); every runtime value (position bounds, cutback instant, type
/// set, batch limit) travels as an <see cref="NpgsqlParameter"/>.
/// </remarks>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The only interpolated identifier is the schema name, validated fail-fast against " +
        "^[a-z_][a-z0-9_]{0,62}$ by SchemaName.Validate in the constructor before any use — the " +
        "sanctioned identifier-interpolation exception (audit S1, R3, security scan #7; " +
        "CONSTITUTION §2 FORBIDDEN). All non-identifier values travel as NpgsqlParameter.")]
public sealed class PostgresSubscriptionSource : ISubscriptionSource
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly TimeSpan _visibilityLag;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a subscription source over <paramref name="dataSource"/> reading the all-stream from
    /// <paramref name="options"/>.<see cref="ActaPostgresOptions.SchemaName"/> (validated fail-fast
    /// here, exactly like <c>PostgresEventStore</c>).
    /// </summary>
    /// <param name="dataSource">The Npgsql data source the source opens read connections from.</param>
    /// <param name="options">Backend options; the schema name is validated fail-fast in this constructor.</param>
    /// <param name="visibilityLag">
    /// The safe high-water-mark cutback: events younger than <c>now - visibilityLag</c> are withheld
    /// from <see cref="ReadBatchAsync"/> so a not-yet-committed transaction cannot leave a hole the
    /// daemon reads past (04-data §3.2). <see cref="TimeSpan.Zero"/> disables the cutback (in-memory
    /// parity — every committed event is immediately safe).
    /// </param>
    /// <param name="timeProvider">
    /// Clock used to compute the cutback instant, injectable for deterministic tests. Must be the same
    /// authority <c>PostgresEventStore</c> stamps <c>created_at</c> with. <see langword="null"/> (the
    /// default) resolves to <see cref="TimeProvider.System"/>.
    /// </param>
    /// <exception cref="ArgumentException">The configured schema name is outside the allow-list.</exception>
    public PostgresSubscriptionSource(
        NpgsqlDataSource dataSource,
        ActaPostgresOptions options,
        TimeSpan visibilityLag,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _schema = SchemaName.Validate(options.SchemaName);
        _visibilityLag = visibilityLag;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StoredEvent> ReadFromAsync(GlobalPosition from, CancellationToken ct = default)
        => ReadFromCoreAsync(from, ct);

    private async IAsyncEnumerable<StoredEvent> ReadFromCoreAsync(
        GlobalPosition from,
        [EnumeratorCancellation] CancellationToken ct)
    {
        const string columns =
            "event_id, stream_id, version, global_position, event_type, schema_version, payload, metadata, created_at";
        var sql =
            $"""
             SELECT {columns}
             FROM {_schema}.events
             WHERE global_position > @from
             ORDER BY global_position ASC
             """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("from", from.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return MapEvent(reader);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<StoredEvent>> ReadBatchAsync(
        GlobalPosition from,
        int maxCount,
        IReadOnlySet<string>? eventTypes = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        ct.ThrowIfCancellationRequested();

        // Safe HWM cutback (P1): withhold events younger than now - VisibilityLag, computed from the
        // same TimeProvider that stamped created_at, so a still-in-flight predecessor position cannot
        // be leapt over. TimeSpan.Zero => cutoff == now => only strictly-past events (in-memory parity).
        var cutoff = _timeProvider.GetUtcNow() - _visibilityLag;

        const string columns =
            "event_id, stream_id, version, global_position, event_type, schema_version, payload, metadata, created_at";
        var sql =
            $"""
             SELECT {columns}
             FROM {_schema}.events
             WHERE global_position > @from
               AND created_at < @cutoff
               AND (@types IS NULL OR event_type = ANY(@types))
             ORDER BY global_position ASC
             LIMIT @maxCount
             """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("from", from.Value);
        command.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = cutoff });
        command.Parameters.Add(new NpgsqlParameter("types", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            // null => match every type; a set (incl. empty) is pushed down as ANY(@types): an empty
            // array makes `event_type = ANY('{}')` false for every row, yielding an empty batch.
            Value = eventTypes is null ? DBNull.Value : eventTypes.ToArray(),
        });
        command.Parameters.AddWithValue("maxCount", maxCount);

        var batch = new List<StoredEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            batch.Add(MapEvent(reader));
        }

        return batch;
    }

    // Identical column order + mapping shape as PostgresEventStore.MapEvent (payload read back as
    // UTF-8 bytes, metadata deserialized from the private jsonb column).
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

    private static readonly JsonSerializerOptions MetadataOptions = new(JsonSerializerDefaults.Web);
}
