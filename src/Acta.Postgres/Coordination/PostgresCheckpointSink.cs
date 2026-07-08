using System.Diagnostics.CodeAnalysis;

using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.Store;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace Acta.Postgres.Coordination;

/// <summary>
/// PostgreSQL-backed <see cref="ICheckpointSink"/> (ADR-004 — pure Npgsql, no ORM): the coordination
/// store that fences projection-checkpoint writes with a leadership <c>owner_token</c> (D5 / ADR-005)
/// across any multi-pod topology (ADR-014). Unlike <c>InMemoryCheckpointSink</c> — a single process has
/// no leader election and so never fences — this backend rejects a <b>zombie leader</b> (a writer that
/// lost leadership but keeps trying to save): its stale, non-advancing write matches zero rows and is
/// turned into <see cref="CheckpointFencedException"/>, the signal the daemon uses to abandon the
/// projection and invalidate its cache (the zombie-guard).
/// <para>
/// <b>Save = one atomic statement (04-data §3.3, fencing CAS).</b> <see cref="SaveAsync"/> is a single
/// upsert-CAS + classify statement — one snapshot, one round-trip, one connection. The
/// <c>ON CONFLICT DO UPDATE … WHERE</c> predicate realizes the authoritative §3.3 grant
/// (<c>owner_token = @me OR owner_token IS NULL OR position &lt; @new</c>) AND-joined with an
/// advance-only guard (<c>position &lt;= @new</c>) that enforces the <see cref="ICheckpointSink"/>
/// "checkpoint only advances" contract the bare §3.3 SQL would otherwise let a same-owner backward save
/// violate (ADR-005). A fresh row inserts; a same-owner advance, a same-owner no-op on an equal
/// position, and a strictly-ahead takeover by a new owner all match one row; anything else matches zero.
/// The current <c>owner_token</c> is read in the same statement (a data-modifying CTE cannot see its own
/// effect, so on the zero-row path the read returns the live blocking value) to classify the outcome
/// without a second, race-prone query.
/// </para>
/// <para>
/// <b>Zero-row classification.</b> A different, non-null owner holding the row ⇒
/// <see cref="CheckpointFencedException"/> (leadership lost). Otherwise (still our token, or an unowned
/// row) the only cause is a backward move ⇒ <see cref="InvalidOperationException"/> (rollback forbidden
/// outside an explicit rebuild — parity with <c>InMemoryCheckpointSink</c>; the exception type is not
/// frozen cross-backend, R-A).
/// </para>
/// <para>
/// <b>Fencing boundary (D5).</b> This is token-only CAS: a writer whose position is <i>strictly ahead</i>
/// of the stored one takes leadership over by the §3.3 <c>position &lt; @new</c> clause — that is the
/// spec, not a leak. Full split-brain protection (only one live leader at a time) is the advisory-lock
/// elector added in tasks 7.5/7.6; this sink fences the checkpoint write, the elector fences the
/// leadership.
/// </para>
/// </summary>
/// <remarks>
/// Only the fail-fast-validated schema name (<see cref="SchemaName.Validate"/>) is ever interpolated
/// into command text; every runtime value (projection name, tenant id, position, owner token) travels as
/// an <see cref="NpgsqlParameter"/>, and the <c>tenant_id =</c> filter is composed exclusively through
/// <see cref="TenantScopedSqlBuilder"/> (ADR-007/ADR-016) so the classify read cannot leak another
/// tenant's token. Diagnostics log only the <c>projectionName</c> (a code identifier, not PII per
/// ADR-008), the tenant scope key, and the applied/fenced outcome — never the <c>ownerToken</c> or the
/// <c>position</c>.
/// </remarks>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The only interpolated identifier is the schema name, validated fail-fast against " +
        "^[a-z_][a-z0-9_]{0,62}$ by SchemaName.Validate in the constructor before any use — the " +
        "sanctioned identifier-interpolation exception (audit S1, R3, security scan #7; " +
        "CONSTITUTION §2 FORBIDDEN). All non-identifier values travel as NpgsqlParameter, and the " +
        "tenant filter is composed through TenantScopedSqlBuilder (ADR-007/ADR-016).")]
public sealed class PostgresCheckpointSink : ICheckpointSink
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresCheckpointSink>? _logger;

    // Schema validated once in the ctor; SQL text (schema name + tenant filter) is built once and
    // cached — no per-call string interpolation.
    private readonly string _loadSql;
    private readonly string _saveSql;

    /// <summary>
    /// Creates a sink over <paramref name="dataSource"/>, persisting into
    /// <paramref name="options"/>.<see cref="ActaPostgresOptions.SchemaName"/> (validated fail-fast
    /// here, exactly like <c>MigrationRunner</c>/<c>PostgresEventStore</c>).
    /// </summary>
    /// <param name="dataSource">The Npgsql data source the sink opens connections from.</param>
    /// <param name="options">Backend options; the schema name is validated fail-fast in this constructor.</param>
    /// <param name="logger">
    /// Optional diagnostics sink. It receives only the <c>projectionName</c>, the tenant scope key, and
    /// the applied/fenced outcome — never the <c>ownerToken</c> or <c>position</c>.
    /// </param>
    /// <exception cref="ArgumentException">The configured schema name is outside the allow-list.</exception>
    public PostgresCheckpointSink(
        NpgsqlDataSource dataSource,
        ActaPostgresOptions options,
        ILogger<PostgresCheckpointSink>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _logger = logger;
        var schema = SchemaName.Validate(options.SchemaName);

        _loadSql =
            $"SELECT position FROM {schema}.checkpoints WHERE {TenantScopedSqlBuilder.ScopedWhere("projection_name = @projection")}";

        // One atomic statement: upsert-CAS (04-data §3.3 grant AND the advance-only guard) in a
        // data-modifying CTE, plus the current owner_token read in the same snapshot. Because a
        // data-modifying CTE's effect is invisible to a sibling read of the same table, on the
        // zero-row (predicate-false) path `current_owner` is the live blocking value — the input to
        // the fenced-vs-backward classification, with no second (race-prone) query.
        _saveSql =
            $"""
             WITH upsert AS (
                 INSERT INTO {schema}.checkpoints AS c (projection_name, tenant_id, position, owner_token, updated_at)
                 VALUES (@projection, @tenant, @position, @owner, now())
                 ON CONFLICT (projection_name, tenant_id) DO UPDATE
                    SET position = @position, owner_token = @owner, updated_at = now()
                  WHERE (c.owner_token = @owner OR c.owner_token IS NULL OR c.position < @position)
                    AND c.position <= @position
                 RETURNING 1 AS applied
             )
             SELECT (SELECT count(*) FROM upsert) AS applied,
                    (SELECT c.owner_token FROM {schema}.checkpoints c
                      WHERE {TenantScopedSqlBuilder.ScopedWhere("projection_name = @projection")}) AS current_owner
             """;
    }

    /// <inheritdoc/>
    public async ValueTask<GlobalPosition?> LoadAsync(string projectionName, string? tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(_loadSql, connection);
        TenantScopedSqlBuilder.BindTenant(command, tenantId);
        command.Parameters.AddWithValue("projection", projectionName);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new GlobalPosition(reader.GetInt64(0));
    }

    /// <inheritdoc/>
    public async ValueTask SaveAsync(
        string projectionName, string? tenantId, GlobalPosition position, string ownerToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(_saveSql, connection);
        TenantScopedSqlBuilder.BindTenant(command, tenantId);
        command.Parameters.AddWithValue("projection", projectionName);
        command.Parameters.AddWithValue("position", position.Value);
        command.Parameters.AddWithValue("owner", ownerToken);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        // The outer SELECT projects two scalar subqueries, so it always yields exactly one row.
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var applied = reader.GetInt64(0);
        var currentOwner = reader.IsDBNull(1) ? null : reader.GetString(1);

        if (applied == 1)
        {
            _logger?.LogDebug(
                "Checkpoint Save projection={Projection} tenant={Tenant} applied=true", projectionName, tenantId ?? string.Empty);
            return;
        }

        // applied == 0: the CAS predicate was false. A different, non-null owner holding the row is a
        // fencing failure (zombie leader); still-our-token (or an unowned row) leaves the backward move
        // as the only possible cause (advance-only guard) — a forbidden rollback.
        if (currentOwner is not null && !string.Equals(currentOwner, ownerToken, StringComparison.Ordinal))
        {
            _logger?.LogWarning(
                "Checkpoint Save projection={Projection} tenant={Tenant} fenced (leadership lost)", projectionName, tenantId ?? string.Empty);
            throw new CheckpointFencedException(projectionName, ownerToken);
        }

        throw new InvalidOperationException(
            $"Checkpoint for '{projectionName}' cannot move backward to {position.Value} " +
            "(no rollback outside an explicit rebuild — ADR-005).");
    }
}
