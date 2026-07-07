using System.Diagnostics.CodeAnalysis;

using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.Store;

using Microsoft.Extensions.Logging;

using Npgsql;

using NpgsqlTypes;

namespace Acta.Postgres.Idempotency;

/// <summary>
/// PostgreSQL-backed <see cref="IIdempotencyStore"/> (ADR-004 — pure Npgsql, no ORM): the default,
/// tenant-scoped command-entry dedup used across any multi-pod topology (ADR-014). The dedup guarantee
/// is realized <b>exclusively</b> by the <c>idempotency</c> primary key
/// <c>(tenant_id, idempotency_key)</c> — never by an in-process cache (ADR-003 Enforcement) — so a
/// retry of a committed command is recognized by every pod over the shared schema, even after a
/// restart.
/// <para>
/// <b>Register (04-data §2, spirit of §3.5).</b> <see cref="TryRegisterAsync"/> is a single atomic
/// <c>INSERT … ON CONFLICT DO UPDATE … WHERE i.expires_at &lt; now()</c>: a new key inserts, an
/// <i>expired</i> entry is lazily re-registered (its result reset to <c>NULL</c>), and an active
/// registration leaves the guarded update matching zero rows. The execute/duplicate outcome is the
/// affected-row count (<c>1</c> ⇒ execute, <c>0</c> ⇒ duplicate) — a duplicate is a boolean, not an
/// exception (ADR-003). Retention is driven by the PostgreSQL server clock (<c>now()</c>).
/// </para>
/// <para>
/// <b>Save / get.</b> The remembered result round-trips through the <c>result</c> <c>bytea</c> column;
/// <see cref="GetResultAsync"/> returns <see langword="null"/> both when the key is unknown and when
/// it is registered but has no saved result yet.
/// </para>
/// </summary>
/// <remarks>
/// Only the fail-fast-validated schema name (<see cref="SchemaName.Validate"/>) is ever interpolated
/// into command text; every runtime value (tenant id, key, retention, result) travels as an
/// <see cref="NpgsqlParameter"/>, and the <c>tenant_id =</c> filter is composed exclusively through
/// <see cref="TenantScopedSqlBuilder"/> (ADR-007/ADR-016). Per the <see cref="IIdempotencyStore"/>
/// no-PII contract, diagnostics log only the tenant scope key and affected-row counts — never the
/// <c>idempotencyKey</c> or the <c>result</c> bytes.
/// </remarks>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The only interpolated identifier is the schema name, validated fail-fast against " +
        "^[a-z_][a-z0-9_]{0,62}$ by SchemaName.Validate in the constructor before any use — the " +
        "sanctioned identifier-interpolation exception (audit S1, R3, security scan #7; " +
        "CONSTITUTION §2 FORBIDDEN). All non-identifier values travel as NpgsqlParameter, and the " +
        "tenant filter is composed through TenantScopedSqlBuilder (ADR-007/ADR-016).")]
public sealed class PostgresIdempotencyStore : IIdempotencyStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresIdempotencyStore>? _logger;

    // Schema validated once in the ctor; SQL text is built once and cached (plan §2.4).
    private readonly string _tryRegisterSql;
    private readonly string _getResultSql;
    private readonly string _saveResultSql;

    /// <summary>
    /// Creates a store over <paramref name="dataSource"/>, persisting into
    /// <paramref name="options"/>.<see cref="ActaPostgresOptions.SchemaName"/> (validated fail-fast
    /// here, exactly like <c>MigrationRunner</c>/<c>PostgresEventStore</c>).
    /// </summary>
    /// <param name="dataSource">The Npgsql data source the store opens connections from.</param>
    /// <param name="options">Backend options; the schema name is validated fail-fast in this constructor.</param>
    /// <param name="logger">
    /// Optional diagnostics sink. Per the <see cref="IIdempotencyStore"/> no-PII contract it receives
    /// only the tenant scope key and affected-row counts — never the <c>idempotencyKey</c> or <c>result</c>.
    /// </param>
    /// <exception cref="ArgumentException">The configured schema name is outside the allow-list.</exception>
    public PostgresIdempotencyStore(
        NpgsqlDataSource dataSource,
        ActaPostgresOptions options,
        ILogger<PostgresIdempotencyStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _logger = logger;
        var schema = SchemaName.Validate(options.SchemaName);

        // Lazy re-registration of an expired entry (race-resilient with housekeeping, 7.8); tenant
        // scoping is structural (PK).
        _tryRegisterSql =
            $"""
             INSERT INTO {schema}.idempotency AS i (tenant_id, idempotency_key, result, expires_at)
             VALUES (@tenant, @key, NULL, now() + @retention)
             ON CONFLICT (tenant_id, idempotency_key) DO UPDATE
                SET expires_at = excluded.expires_at, result = NULL
              WHERE i.expires_at < now()
             """;

        _getResultSql =
            $"SELECT result FROM {schema}.idempotency WHERE {TenantScopedSqlBuilder.ScopedWhere("idempotency_key = @key")}";

        _saveResultSql =
            $"UPDATE {schema}.idempotency SET result = @result WHERE {TenantScopedSqlBuilder.ScopedWhere("idempotency_key = @key")}";
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryRegisterAsync(
        string idempotencyKey, TimeSpan retention, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(_tryRegisterSql, connection);
        TenantScopedSqlBuilder.BindTenant(command, tenantId);
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.Add(new NpgsqlParameter("retention", NpgsqlDbType.Interval) { Value = retention });

        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        var registered = rows == 1;
        _logger?.LogDebug("Idempotency TryRegister tenant={Tenant} registered={Registered}", tenantId ?? string.Empty, registered);
        return registered;
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> GetResultAsync(
        string idempotencyKey, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(_getResultSql, connection);
        TenantScopedSqlBuilder.BindTenant(command, tenantId);
        command.Parameters.AddWithValue("key", idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return new ReadOnlyMemory<byte>(reader.GetFieldValue<byte[]>(0));
    }

    /// <inheritdoc/>
    public async ValueTask SaveResultAsync(
        string idempotencyKey, ReadOnlyMemory<byte> result, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(_saveResultSql, connection);
        TenantScopedSqlBuilder.BindTenant(command, tenantId);
        command.Parameters.AddWithValue("key", idempotencyKey);
        command.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Bytea) { Value = result.ToArray() });

        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger?.LogDebug("Idempotency SaveResult tenant={Tenant} rows={Rows}", tenantId ?? string.Empty, rows);
    }
}
