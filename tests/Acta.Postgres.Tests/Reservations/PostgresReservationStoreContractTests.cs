using Acta.Abstractions;
using Acta.Postgres.Reservations;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.Reservations.Contracts;

using Xunit;

namespace Acta.Postgres.Tests.Reservations;

/// <summary>
/// Runs the shared <see cref="ReservationStoreContractTests"/> suite against a real PostgreSQL backend
/// (TESTING-SPEC §5.1/§6.1): the exact same facts that will constrain the in-memory backend (task 8.5)
/// constrain <see cref="PostgresReservationStore"/>. Each test gets a fresh, migrated schema in the
/// shared Testcontainers container (isolation by schema name, not by container).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresReservationStoreContractTests(PostgresFixture fixture) : ReservationStoreContractTests
{
    private readonly PostgresFixture _fixture = fixture;

    protected override async ValueTask<IReservationStore> CreateStoreAsync()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, TestContext.Current.CancellationToken);
        return new PostgresReservationStore(_fixture.DataSource, options);
    }
}
