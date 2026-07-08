using Acta.Postgres.Configuration;
using Acta.Postgres.Migrations;

namespace Acta.Postgres.Tests.Infrastructure;

/// <summary>
/// Test helper: provisions a fresh, migrated schema in the shared Testcontainers container so each
/// contract/integration class runs against a clean copy of the R3 tables without spinning a new
/// container (isolation by schema name, per <see cref="PostgresFixture"/>). Shared by the reservation
/// and idempotency suites (task 7.7) so the "new schema + run migrations" block is written once.
/// </summary>
public static class PostgresSchemaSetup
{
    /// <summary>
    /// Creates a unique, allow-list-valid schema and applies every migration to it, returning the
    /// options bound to that schema (ready to construct a store over
    /// <see cref="PostgresFixture.DataSource"/>).
    /// </summary>
    /// <param name="fixture">The shared container fixture supplying the data source.</param>
    /// <param name="ct">A token to cancel the migration.</param>
    /// <returns>Options whose <see cref="ActaPostgresOptions.SchemaName"/> names the migrated schema.</returns>
    public static async ValueTask<ActaPostgresOptions> MigrateFreshSchemaAsync(PostgresFixture fixture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var options = new ActaPostgresOptions { SchemaName = PostgresFixture.NewSchemaName() };
        await new MigrationRunner(fixture.DataSource, options).MigrateAsync(ct);
        return options;
    }
}
