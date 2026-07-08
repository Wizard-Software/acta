using Acta.Abstractions;
using Acta.Postgres.Idempotency;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.Idempotency.Contracts;

using Xunit;

namespace Acta.Postgres.Tests.Idempotency;

/// <summary>
/// Runs the shared <see cref="IdempotencyStoreContractTests"/> suite against a real PostgreSQL backend
/// (TESTING-SPEC §5.1/§6.1): the exact same facts that will constrain the in-memory backend (task 8.5)
/// constrain <see cref="PostgresIdempotencyStore"/>. Each test gets a fresh, migrated schema in the
/// shared Testcontainers container (isolation by schema name, not by container).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresIdempotencyStoreContractTests(PostgresFixture fixture) : IdempotencyStoreContractTests
{
    private readonly PostgresFixture _fixture = fixture;

    protected override async ValueTask<IIdempotencyStore> CreateStoreAsync()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, TestContext.Current.CancellationToken);
        return new PostgresIdempotencyStore(_fixture.DataSource, options);
    }
}
