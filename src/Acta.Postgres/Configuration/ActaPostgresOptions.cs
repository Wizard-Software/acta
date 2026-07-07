namespace Acta.Postgres.Configuration;

/// <summary>
/// Configuration for the PostgreSQL backend (<c>AddActaPostgres(...)</c>, MODULE-INTERFACES
/// "Rejestracja DI").
/// <para>
/// This type carries only the configuration a component in the Postgres adapter actually reads:
/// the schema name consumed by <see cref="Acta.Postgres.Migrations.MigrationRunner"/> and every
/// store, and <see cref="AutoMigrate"/>, read by the <c>MigrationHostedService</c> registered by
/// <c>AddActaPostgres</c> (task 7.9). This mirrors how <c>ActaOptions</c> was introduced
/// Tier-1-scoped in task 3.3 and extended later — no public member is added without a reader
/// (CONSTITUTION §2 ASK FIRST).
/// </para>
/// </summary>
/// <remarks>
/// <b>Connection pool sizing (06-cross-cutting §4)</b> is deliberately doc-only — there is no
/// <c>MaxPoolSize</c> member on this type, because Npgsql already reads pool sizing from the
/// connection string (or from a host-owned <see cref="Npgsql.NpgsqlDataSource"/> the host built
/// itself), and a duplicate knob here would be a second, conflicting source of truth. Size the
/// pool via the connection string's <c>Maximum Pool Size</c> (Npgsql default: <c>100</c>) using
/// the formula <c>MaxPoolSize &gt;= projections-in-catch-up + host-append-parallelism + 2</c> —
/// enough connections for every catch-up projection to poll concurrently, for the host's own
/// append concurrency, and headroom for <c>MigrationRunner</c> plus incidental pool churn.
/// </remarks>
public sealed class ActaPostgresOptions
{
    /// <summary>
    /// The PostgreSQL schema that owns every Acta table (default <c>"acta"</c>). Configurable
    /// (04-data §1) but validated fail-fast against <see cref="SchemaName.Validate"/> before it is
    /// interpolated into any SQL identifier — a value outside <c>^[a-z_][a-z0-9_]{0,62}$</c> is a
    /// configuration error, never silently sanitized (audit S1, revision R3, security scan #7).
    /// </summary>
    public string SchemaName { get; set; } = "acta";

    /// <summary>
    /// Whether <c>AddActaPostgres</c>'s hosted service applies pending schema migrations
    /// automatically at host startup (default <see langword="true"/>).
    /// <para>
    /// <see langword="true"/> is intended for development: the connection string's role needs DDL
    /// privileges (<c>acta_migrator</c>, 04-data §4.1). <see langword="false"/> is recommended for
    /// production (04-data §4) — migrations run as a dedicated step in the CD pipeline under the
    /// <c>acta_migrator</c> role, and the runtime connection string carries only the least-privilege
    /// <c>acta_runtime</c> role (DML-only, no DDL — see
    /// <c>Roles/acta-least-privilege-roles.sql</c>). When <see langword="false"/>, the hosted
    /// service logs a startup warning instead of migrating.
    /// </para>
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>
    /// Retention and cleanup policy for the auxiliary tables (outbox, idempotency, reservations,
    /// dead-letter), read by the housekeeping loop (<see cref="Acta.Postgres.Housekeeping.Housekeeper"/>
    /// driven by <c>HousekeeperHostedService</c>, task 7.8; 04-data §3.6, 03-contracts §2). Defaults are
    /// an explicit retention policy; a non-positive <see cref="HousekeepingOptions.Interval"/> disables
    /// the loop and any per-table <see cref="System.TimeSpan.Zero"/> disables that table's purge.
    /// </summary>
    public HousekeepingOptions Housekeeping { get; set; } = new();
}
