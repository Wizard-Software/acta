namespace Acta.Postgres.Configuration;

/// <summary>
/// Configuration for the PostgreSQL backend (<c>AddActaPostgres(...)</c>, MODULE-INTERFACES
/// "Rejestracja DI").
/// <para>
/// Task-7.1 shape: this type carries only the configuration a component in Feature 7.1 actually
/// reads — the schema name consumed by <see cref="Acta.Postgres.Migrations.MigrationRunner"/>.
/// Fields describing not-yet-existing components (e.g. <c>AutoMigrate</c>, read by
/// <c>AddActaPostgres</c> at host startup, and the Npgsql pool guidance) are deliberately omitted
/// to avoid premature public members with no reader (CONSTITUTION §2 ASK FIRST) — they arrive as
/// non-breaking additive properties in task 7.9 (the DI composition root). This mirrors how
/// <c>ActaOptions</c> was introduced Tier-1-scoped in task 3.3 and extended later.
/// </para>
/// </summary>
public sealed class ActaPostgresOptions
{
    /// <summary>
    /// The PostgreSQL schema that owns every Acta table (default <c>"acta"</c>). Configurable
    /// (04-data §1) but validated fail-fast against <see cref="SchemaName.Validate"/> before it is
    /// interpolated into any SQL identifier — a value outside <c>^[a-z_][a-z0-9_]{0,62}$</c> is a
    /// configuration error, never silently sanitized (audit S1, revision R3, security scan #7).
    /// </summary>
    public string SchemaName { get; set; } = "acta";
}
