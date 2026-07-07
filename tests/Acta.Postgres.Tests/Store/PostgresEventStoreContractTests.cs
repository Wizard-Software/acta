using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.Migrations;
using Acta.Postgres.Store;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.Store.Contracts;

using Xunit;

namespace Acta.Postgres.Tests.Store;

/// <summary>
/// Runs the shared <see cref="EventStoreContractTests"/> suite against a real PostgreSQL backend
/// (AK-3 parity, task 7.2): the exact same facts that constrain <c>InMemoryEventStore</c> constrain
/// <see cref="PostgresEventStore"/>. Each test gets a fresh, migrated schema in the shared
/// Testcontainers container (isolation by schema name, not by container), so the whole matrix runs
/// against a clean event store every time.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresEventStoreContractTests(PostgresFixture fixture) : EventStoreContractTests
{
    private readonly PostgresFixture _fixture = fixture;

    protected override async ValueTask<IEventStore> CreateStoreAsync()
    {
        var options = new ActaPostgresOptions { SchemaName = PostgresFixture.NewSchemaName() };
        await new MigrationRunner(_fixture.DataSource, options).MigrateAsync(TestContext.Current.CancellationToken);
        return new PostgresEventStore(_fixture.DataSource, options);
    }
}
