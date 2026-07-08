using System.Diagnostics.CodeAnalysis;

using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.Store;

using Microsoft.Extensions.Logging;

using Npgsql;

using NpgsqlTypes;

namespace Acta.Postgres.Reservations;

/// <summary>
/// PostgreSQL-backed <see cref="IReservationStore"/> (ADR-004 — pure Npgsql, no ORM): the default,
/// tenant-scoped uniqueness store used across any multi-pod topology (ADR-014). The uniqueness
/// guarantee is realized <b>exclusively</b> by the <c>reservations</c> primary key
/// <c>(tenant_id, scope, value)</c> and set-based statements — never by application logic or an
/// in-process cache (ADR-009 Enforcement) — so it survives a restart and is visible to every pod over
/// the shared schema.
/// <para>
/// <b>Reserve (04-data §3.5).</b> <see cref="TryReserveAsync"/> is a single atomic
/// <c>INSERT … ON CONFLICT DO UPDATE … WHERE r.confirmed = false AND r.expires_at &lt; now()</c>: a
/// free value inserts, an <i>unconfirmed and expired</i> value is lazily taken over, and an active or
/// confirmed value leaves the guarded update matching zero rows. The reserved/not-reserved outcome is
/// the affected-row count (<c>1</c> ⇒ reserved, <c>0</c> ⇒ collision) — a collision is a boolean, not
/// an exception (ADR-009). Expiry is driven by the PostgreSQL server clock (<c>now()</c>), never a
/// client clock.
/// </para>
/// <para>
/// <b>Confirm / release.</b> Both are owner-guarded (<c>owner_id = @owner AND confirmed = false</c>):
/// the loser of a takeover can neither confirm nor release the new owner's reservation, and a
/// confirmed reservation is permanent (never released) — each is a silent no-op that affects zero
/// rows rather than throwing.
/// </para>
/// </summary>
/// <remarks>
/// Only the fail-fast-validated schema name (<see cref="SchemaName.Validate"/>) is ever interpolated
/// into command text; every runtime value (tenant id, scope, value, owner, ttl) travels as an
/// <see cref="NpgsqlParameter"/>, and the <c>tenant_id =</c> filter is composed exclusively through
/// <see cref="TenantScopedSqlBuilder"/> (ADR-007/ADR-016). Per the <see cref="IReservationStore"/>
/// no-PII contract, diagnostics log only the tenant scope key and affected-row counts — never
/// <c>scope</c>, <c>value</c>, or <c>ownerId</c>.
/// </remarks>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The only interpolated identifier is the schema name, validated fail-fast against " +
        "^[a-z_][a-z0-9_]{0,62}$ by SchemaName.Validate in the constructor before any use — the " +
        "sanctioned identifier-interpolation exception (audit S1, R3, security scan #7; " +
        "CONSTITUTION §2 FORBIDDEN). All non-identifier values travel as NpgsqlParameter, and the " +
        "tenant filter is composed through TenantScopedSqlBuilder (ADR-007/ADR-016).")]
public sealed class PostgresReservationStore : IReservationStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresReservationStore>? _logger;

    // Schema validated once in the ctor; SQL text (schema name + tenant filter) is built once and
    // cached — no per-call string interpolation (plan §2.4).
    private readonly string _tryReserveSql;
    private readonly string _confirmSql;
    private readonly string _releaseSql;

    /// <summary>
    /// Creates a store over <paramref name="dataSource"/>, persisting into
    /// <paramref name="options"/>.<see cref="ActaPostgresOptions.SchemaName"/> (validated fail-fast
    /// here, exactly like <c>MigrationRunner</c>/<c>PostgresEventStore</c>).
    /// </summary>
    /// <param name="dataSource">The Npgsql data source the store opens connections from.</param>
    /// <param name="options">Backend options; the schema name is validated fail-fast in this constructor.</param>
    /// <param name="logger">
    /// Optional diagnostics sink. Per the <see cref="IReservationStore"/> no-PII contract it receives
    /// only the tenant scope key and affected-row counts — never <c>scope</c>/<c>value</c>/<c>ownerId</c>.
    /// </param>
    /// <exception cref="ArgumentException">The configured schema name is outside the allow-list.</exception>
    public PostgresReservationStore(
        NpgsqlDataSource dataSource,
        ActaPostgresOptions options,
        ILogger<PostgresReservationStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _logger = logger;
        var schema = SchemaName.Validate(options.SchemaName);

        // Lazy takeover of an unconfirmed, expired reservation; tenant scoping is structural (PK).
        _tryReserveSql =
            $"""
             INSERT INTO {schema}.reservations AS r (tenant_id, scope, value, owner_id, expires_at)
             VALUES (@tenant, @scope, @value, @owner, now() + @ttl)
             ON CONFLICT (tenant_id, scope, value) DO UPDATE
                SET owner_id = excluded.owner_id, expires_at = excluded.expires_at
              WHERE r.confirmed = false AND r.expires_at < now()
             """;

        _confirmSql =
            $"""
             UPDATE {schema}.reservations
                SET confirmed = true, expires_at = NULL
              WHERE {TenantScopedSqlBuilder.ScopedWhere("scope = @scope", "value = @value", "owner_id = @owner", "confirmed = false")}
             """;

        _releaseSql =
            $"""
             DELETE FROM {schema}.reservations
              WHERE {TenantScopedSqlBuilder.ScopedWhere("scope = @scope", "value = @value", "owner_id = @owner", "confirmed = false")}
             """;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> TryReserveAsync(
        string scope, string value, string ownerId, TimeSpan ttl, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(_tryReserveSql, connection);
        TenantScopedSqlBuilder.BindTenant(command, tenantId);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.Add(new NpgsqlParameter("ttl", NpgsqlDbType.Interval) { Value = ttl });

        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        var reserved = rows == 1;
        _logger?.LogDebug("Reservation TryReserve tenant={Tenant} reserved={Reserved}", tenantId ?? string.Empty, reserved);
        return reserved;
    }

    /// <inheritdoc/>
    public async ValueTask ConfirmAsync(
        string scope, string value, string ownerId, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(_confirmSql, connection);
        TenantScopedSqlBuilder.BindTenant(command, tenantId);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("owner", ownerId);

        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger?.LogDebug("Reservation Confirm tenant={Tenant} confirmed={Rows}", tenantId ?? string.Empty, rows);
    }

    /// <inheritdoc/>
    public async ValueTask ReleaseAsync(
        string scope, string value, string ownerId, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(_releaseSql, connection);
        TenantScopedSqlBuilder.BindTenant(command, tenantId);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("owner", ownerId);

        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger?.LogDebug("Reservation Release tenant={Tenant} released={Rows}", tenantId ?? string.Empty, rows);
    }
}
