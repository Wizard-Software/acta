using System.Diagnostics.CodeAnalysis;

using Acta.Postgres.Configuration;

using Microsoft.Extensions.Logging;

using Npgsql;

using NpgsqlTypes;

namespace Acta.Postgres.Housekeeping;

/// <summary>
/// Purges the auxiliary PostgreSQL tables (04-data §3.6) as a <b>single-active</b> sweep gated on the
/// session-level advisory lock <c>{schema}:housekeeping</c> — across any multi-pod topology (ADR-014)
/// exactly one pod runs a given sweep, the rest skip. Deletes published outbox rows past
/// <see cref="HousekeepingOptions.PublishedOutboxRetention"/>, expired idempotency entries, expired
/// unconfirmed reservations past <see cref="HousekeepingOptions.ExpiredReservationsSweep"/>, and
/// dead-letter rows past <see cref="HousekeepingOptions.DeadLetterRetention"/>. Retention is evaluated
/// against the PostgreSQL server clock (<c>now()</c>).
/// <para>
/// <b>Single-active (ADR-005 / 04-data §3.4 lock idiom).</b> Each <see cref="SweepAsync"/> opens one
/// connection, runs <c>pg_try_advisory_lock(hashtextextended(@key, 0))</c> with a schema-namespaced key
/// (skan sec #2 — advisory locks live in a database-global space; without the <see cref="SchemaName"/>
/// namespace two Acta instances on one database would collide), does every DELETE on that same pinned
/// connection, then <c>pg_advisory_unlock</c>s in a <c>finally</c>. A lost session releases the lock in
/// the database automatically — a crashed housekeeper never wedges the sweep.
/// </para>
/// <para>
/// <b>Batched deletes.</b> Every purge deletes in bounded <see cref="PurgeBatchSize"/> chunks
/// (<c>DELETE … WHERE ctid IN (SELECT ctid … LIMIT @batch)</c>) looping until a short batch, so a large
/// backlog never runs one unbounded, long-locking statement. Each table's total is reported on
/// <see cref="HousekeepingMetrics"/> (<c>acta.housekeeping.purged</c>, tagged by table).
/// </para>
/// </summary>
/// <remarks>
/// Only the fail-fast-validated schema name (<see cref="SchemaName.Validate"/>) is ever interpolated
/// into command text; the advisory-lock key travels as an <see cref="NpgsqlParameter"/> and is hashed
/// by <c>hashtextextended</c> in the database, and every retention value travels as an
/// <see cref="NpgsqlParameter"/> of type <see cref="NpgsqlDbType.Interval"/>.
/// </remarks>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The only interpolated identifier is the schema name, validated fail-fast against " +
        "^[a-z_][a-z0-9_]{0,62}$ by SchemaName.Validate in the constructor before any use — the " +
        "sanctioned identifier-interpolation exception (audit S1, R3, security scan #7; " +
        "CONSTITUTION §2 FORBIDDEN). The lock key and every retention value travel as NpgsqlParameter.")]
public sealed class Housekeeper
{
    /// <summary>Max rows deleted per DELETE statement — the sweep loops until a short batch.</summary>
    internal const int PurgeBatchSize = 1000;

    private const string TryLockSql = "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))";
    private const string UnlockSql = "SELECT pg_advisory_unlock(hashtextextended(@key, 0))";

    private readonly NpgsqlDataSource _dataSource;
    private readonly ActaPostgresOptions _options;
    private readonly HousekeepingMetrics? _metrics;
    private readonly ILogger<Housekeeper>? _logger;

    private readonly string _lockKey;
    private readonly string _purgeOutboxSql;
    private readonly string _purgeIdempotencySql;
    private readonly string _purgeReservationsSql;
    private readonly string _purgeDeadLetterSql;

    /// <summary>
    /// Creates a housekeeper over <paramref name="dataSource"/>, sweeping the tables in
    /// <paramref name="options"/>.<see cref="ActaPostgresOptions.SchemaName"/> (validated fail-fast
    /// here, exactly like <c>MigrationRunner</c>/<c>PostgresReservationStore</c>).
    /// </summary>
    /// <param name="dataSource">The Npgsql data source the sweep opens its pinned connection from.</param>
    /// <param name="options">Backend options; the schema name is validated fail-fast in this constructor and the retention policy is read from <see cref="ActaPostgresOptions.Housekeeping"/>.</param>
    /// <param name="metrics">Optional metrics owner recording <c>acta.housekeeping.purged</c>; <see langword="null"/> disables metric emission.</param>
    /// <param name="logger">Optional diagnostics sink — receives only the schema name and row counts (no PII).</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The configured schema name is outside the allow-list.</exception>
    public Housekeeper(
        NpgsqlDataSource dataSource,
        ActaPostgresOptions options,
        HousekeepingMetrics? metrics = null,
        ILogger<Housekeeper>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _options = options;
        _metrics = metrics;
        _logger = logger;

        var schema = SchemaName.Validate(options.SchemaName);
        _lockKey = $"{schema}:housekeeping";

        // Built once in the ctor; schema is the only interpolated identifier (validated above). Each
        // DELETE is batched by ctid so a large backlog never runs one unbounded, long-locking statement.
        _purgeOutboxSql =
            $"""
             DELETE FROM {schema}.outbox
              WHERE ctid IN (
                    SELECT ctid FROM {schema}.outbox
                     WHERE published_at IS NOT NULL AND published_at < now() - @retention
                     LIMIT @batch)
             """;

        _purgeIdempotencySql =
            $"""
             DELETE FROM {schema}.idempotency
              WHERE ctid IN (
                    SELECT ctid FROM {schema}.idempotency
                     WHERE expires_at < now()
                     LIMIT @batch)
             """;

        _purgeReservationsSql =
            $"""
             DELETE FROM {schema}.reservations
              WHERE ctid IN (
                    SELECT ctid FROM {schema}.reservations
                     WHERE confirmed = false AND expires_at < now() - @retention
                     LIMIT @batch)
             """;

        _purgeDeadLetterSql =
            $"""
             DELETE FROM {schema}.projection_dead_letter
              WHERE ctid IN (
                    SELECT ctid FROM {schema}.projection_dead_letter
                     WHERE first_failed_at < now() - @retention
                     LIMIT @batch)
             """;
    }

    /// <summary>
    /// Runs one single-active housekeeping pass: try to acquire <c>{schema}:housekeeping</c>; if held
    /// elsewhere, return <see cref="HousekeepingReport.Skipped"/>; otherwise purge every enabled
    /// auxiliary table and return the per-table counts.
    /// </summary>
    /// <param name="ct">A token to cancel the sweep.</param>
    /// <returns>The pass outcome (lock held + per-table purge counts, or skipped).</returns>
    public async Task<HousekeepingReport> SweepAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var hk = _options.Housekeeping;

        // Pinned for the whole pass: pg_try_advisory_lock is session-scoped, so the lock, every DELETE,
        // and pg_advisory_unlock must all run on this one connection.
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        if (!await TryAcquireAsync(connection, ct).ConfigureAwait(false))
        {
            _logger?.LogDebug("Housekeeping skipped for schema {Schema} — sweep held by another pod.", _options.SchemaName);
            return HousekeepingReport.Skipped;
        }

        try
        {
            var outbox = hk.PublishedOutboxRetention > TimeSpan.Zero
                ? await PurgeBatchedAsync(connection, _purgeOutboxSql, hk.PublishedOutboxRetention, ct).ConfigureAwait(false)
                : 0;

            // Idempotency has no retention knob — it always sweeps expires_at < now() when the pass runs.
            var idempotency = await PurgeBatchedAsync(connection, _purgeIdempotencySql, retention: null, ct).ConfigureAwait(false);

            var reservations = hk.ExpiredReservationsSweep > TimeSpan.Zero
                ? await PurgeBatchedAsync(connection, _purgeReservationsSql, hk.ExpiredReservationsSweep, ct).ConfigureAwait(false)
                : 0;

            var deadLetter = hk.DeadLetterRetention > TimeSpan.Zero
                ? await PurgeBatchedAsync(connection, _purgeDeadLetterSql, hk.DeadLetterRetention, ct).ConfigureAwait(false)
                : 0;

            _metrics?.RecordPurged("outbox", outbox);
            _metrics?.RecordPurged("idempotency", idempotency);
            _metrics?.RecordPurged("reservations", reservations);
            _metrics?.RecordPurged("projection_dead_letter", deadLetter);

            var report = new HousekeepingReport(true, outbox, idempotency, reservations, deadLetter);
            if (report.TotalPurged > 0)
            {
                _logger?.LogInformation(
                    "Housekeeping sweep purged {Total} rows for schema {Schema} (outbox={Outbox}, idempotency={Idempotency}, reservations={Reservations}, dead_letter={DeadLetter}).",
                    report.TotalPurged, _options.SchemaName, outbox, idempotency, reservations, deadLetter);
            }

            return report;
        }
        finally
        {
            await ReleaseAsync(connection).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryAcquireAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(TryLockSql, connection);
        command.Parameters.AddWithValue("key", _lockKey);
        return (bool)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private async Task ReleaseAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand(UnlockSql, connection);
            command.Parameters.AddWithValue("key", _lockKey);
            // No cancellation token: the unlock must run even if the sweep was cancelled, otherwise the
            // lock would linger until the session is reset. A dead session already released it in the DB.
            await command.ExecuteScalarAsync().ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Session already gone → PostgreSQL has already released the lock; nothing to unlock.
        }
    }

    private static async Task<long> PurgeBatchedAsync(
        NpgsqlConnection connection, string sql, TimeSpan? retention, CancellationToken ct)
    {
        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await using var command = new NpgsqlCommand(sql, connection);
            if (retention is { } r)
            {
                command.Parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval) { Value = r });
            }
            command.Parameters.AddWithValue("batch", PurgeBatchSize);

            var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            total += rows;

            // A short batch means the predicate is drained — stop looping.
            if (rows < PurgeBatchSize)
            {
                return total;
            }
        }
    }
}
