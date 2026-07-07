namespace Acta.Postgres.Migrations;

/// <summary>
/// A single versioned schema migration: its ordinal <paramref name="Version"/> (parsed from the
/// <c>NNNN_</c> filename prefix), a human-readable <paramref name="Name"/>, and the raw
/// <paramref name="Sql"/> (a schema-templated script whose <c>{schema}</c> token the
/// <see cref="MigrationRunner"/> substitutes with the validated schema name before execution).
/// </summary>
/// <param name="Version">Monotonic migration version; applied in ascending order, recorded in <c>{schema}.__migrations</c>.</param>
/// <param name="Name">Migration name (the filename without extension), stored for diagnostics.</param>
/// <param name="Sql">The migration script with the literal <c>{schema}</c> token in place of the schema name.</param>
public readonly record struct Migration(long Version, string Name, string Sql);
