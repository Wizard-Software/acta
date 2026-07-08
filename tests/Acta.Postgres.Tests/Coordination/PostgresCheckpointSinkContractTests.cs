using Acta.Abstractions;
using Acta.Postgres.Coordination;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.Subscriptions.Contracts;

using Xunit;

namespace Acta.Postgres.Tests.Coordination;

/// <summary>
/// Runs the shared <see cref="CheckpointSinkContractTests"/> suite against a real PostgreSQL backend
/// (TESTING-SPEC §5.1): the exact same facts that constrain <c>InMemoryCheckpointSink</c> constrain
/// <see cref="PostgresCheckpointSink"/> — ratifying the cross-backend contract deferred to Feature 7.
/// Each test gets a fresh, migrated schema in the shared Testcontainers container (isolation by schema
/// name, not by container).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresCheckpointSinkContractTests(PostgresFixture fixture) : CheckpointSinkContractTests
{
    private readonly PostgresFixture _fixture = fixture;

    protected override async ValueTask<ICheckpointSink> CreateSinkAsync()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, TestContext.Current.CancellationToken);
        return new PostgresCheckpointSink(_fixture.DataSource, options);
    }
}
