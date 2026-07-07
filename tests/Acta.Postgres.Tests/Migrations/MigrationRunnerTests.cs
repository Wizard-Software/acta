using System.Diagnostics.CodeAnalysis;

using Acta.Postgres.Configuration;
using Acta.Postgres.Migrations;
using Acta.Postgres.Tests.Infrastructure;

using Npgsql;

using Xunit;

namespace Acta.Postgres.Tests.Migrations;

/// <summary>
/// Integration tests for <see cref="MigrationRunner"/> on real PostgreSQL (Testcontainers): the
/// 0001 DDL shape (R3), idempotency, multi-pod safety (advisory lock), schema templating, and
/// transactional-DDL atomicity. Each test uses its own schema for isolation on the shared container.
/// </summary>
[Collection(PostgresCollection.Name)]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "Schema names come from PostgresFixture.NewSchemaName (allow-list-valid, " +
        "test-controlled); the queries interpolate only that identifier and read information_schema.")]
public sealed class MigrationRunnerTests(PostgresFixture fixture)
{
    private static readonly string[] ExpectedDomainTables =
    [
        "checkpoints", "events", "idempotency", "outbox",
        "projection_dead_letter", "reservations", "snapshots", "streams",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MigrationRunner CreateRunner(string schema) =>
        new(fixture.DataSource, new ActaPostgresOptions { SchemaName = schema });

    [Fact]
    public async Task MigrateAsync_EmptyDatabase_CreatesEightDomainTables()
    {
        var schema = PostgresFixture.NewSchemaName();

        await CreateRunner(schema).MigrateAsync(Ct);

        var tables = await GetTableNamesAsync(schema);
        tables.Should().Contain(ExpectedDomainTables);
        tables.Where(t => t != "__migrations").Should().HaveCount(8);
    }

    [Fact]
    public async Task MigrateAsync_EmptyDatabase_RecordsMigration0001()
    {
        var schema = PostgresFixture.NewSchemaName();

        await CreateRunner(schema).MigrateAsync(Ct);

        var (version, name) = await GetSingleMigrationAsync(schema);
        version.Should().Be(1);
        name.Should().Be("0001_initial_schema");
    }

    [Fact]
    public async Task MigrateAsync_RunTwiceSameSchema_IsIdempotent()
    {
        var schema = PostgresFixture.NewSchemaName();
        var runner = CreateRunner(schema);

        await runner.MigrateAsync(Ct);
        await runner.MigrateAsync(Ct); // must not throw and must not double-record

        (await CountMigrationsAsync(schema)).Should().Be(1);
        (await GetTableNamesAsync(schema)).Where(t => t != "__migrations").Should().HaveCount(8);
    }

    [Fact]
    public async Task MigrateAsync_ConcurrentRunners_AppliesExactlyOnce()
    {
        var schema = PostgresFixture.NewSchemaName();

        // Two "pods" racing on the same schema. The transaction-scoped advisory lock serializes
        // them: without it both would read an empty applied-set and the second INSERT would hit a
        // primary-key violation on __migrations.
        await Task.WhenAll(
            CreateRunner(schema).MigrateAsync(Ct),
            CreateRunner(schema).MigrateAsync(Ct));

        (await CountMigrationsAsync(schema)).Should().Be(1);
        (await GetTableNamesAsync(schema)).Where(t => t != "__migrations").Should().HaveCount(8);
    }

    [Fact]
    public async Task MigrateAsync_R3Shape_HasTenantAwareKeysAndConstraints()
    {
        var schema = PostgresFixture.NewSchemaName();

        await CreateRunner(schema).MigrateAsync(Ct);

        (await GetPrimaryKeyColumnsAsync(schema, "reservations"))
            .Should().Equal("tenant_id", "scope", "value");
        (await GetPrimaryKeyColumnsAsync(schema, "idempotency"))
            .Should().Equal("tenant_id", "idempotency_key");
        (await GetPrimaryKeyColumnsAsync(schema, "checkpoints"))
            .Should().Equal("projection_name", "tenant_id");

        (await ConstraintExistsAsync(schema, "events", "uq_events_stream_version")).Should().BeTrue();
        (await ConstraintExistsAsync(schema, "events", "uq_events_stream_eventid")).Should().BeTrue();
        (await ColumnExistsAsync(schema, "outbox", "tenant_id")).Should().BeTrue();
    }

    [Fact]
    public async Task MigrateAsync_CustomValidSchemaName_CreatesTablesInThatSchema()
    {
        var schema = "acta_custom_" + Guid.NewGuid().ToString("N")[..12];

        await CreateRunner(schema).MigrateAsync(Ct);

        (await GetTableNamesAsync(schema)).Should().Contain("streams");
    }

    [Fact]
    public async Task MigrateAsync_TransactionalDdl_RollsBackAtomicallyOnFailure()
    {
        var schema = PostgresFixture.NewSchemaName();
        // The second statement references an undefined type; the migration must fail and, because
        // the whole run is one transaction (PostgreSQL transactional DDL), leave nothing behind.
        var broken = new List<Migration>
        {
            new(1, "0001_broken",
                "CREATE TABLE {schema}.good_table (id int);" +
                "CREATE TABLE {schema}.bad_table (id nonexistent_type);"),
        };
        var runner = new MigrationRunner(
            fixture.DataSource, new ActaPostgresOptions { SchemaName = schema }, broken);

        await Awaiting(() => runner.MigrateAsync(Ct)).Should().ThrowAsync<PostgresException>();

        // good_table was created before the failing statement — a rollback must have removed it.
        (await ColumnExistsAsync(schema, "good_table", "id")).Should().BeFalse();
    }

    // --- query helpers ---------------------------------------------------------------------

    private async Task<List<string>> GetTableNamesAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema", connection);
        command.Parameters.AddWithValue("schema", schema);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<List<string>> GetPrimaryKeyColumnsAsync(string schema, string table)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND tc.table_schema = @schema AND tc.table_name = @table
            ORDER BY kcu.ordinal_position
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private async Task<bool> ConstraintExistsAsync(string schema, string table, string constraintName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.table_constraints
            WHERE table_schema = @schema AND table_name = @table AND constraint_name = @name
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("name", constraintName);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct)) > 0;
    }

    private async Task<bool> ColumnExistsAsync(string schema, string table, string column)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table AND column_name = @column
            """, connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct)) > 0;
    }

    private async Task<long> CountMigrationsAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {schema}.__migrations", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
    }

    private async Task<(long Version, string Name)> GetSingleMigrationAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT version, name FROM {schema}.__migrations ORDER BY version", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        return (reader.GetInt64(0), reader.GetString(1));
    }
}
