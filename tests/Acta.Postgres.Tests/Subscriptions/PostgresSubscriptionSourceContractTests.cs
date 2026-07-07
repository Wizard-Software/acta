using Acta.Abstractions;
using Acta.Postgres.Store;
using Acta.Postgres.Subscriptions;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.Subscriptions.Contracts;

using Xunit;

namespace Acta.Postgres.Tests.Subscriptions;

/// <summary>
/// Runs the shared <see cref="SubscriptionSourceContractTests"/> suite against a real PostgreSQL
/// backend (AK-3 parity, task 7.3): the exact same facts that constrain
/// <c>InMemorySubscriptionSource</c> constrain <see cref="PostgresSubscriptionSource"/>. Each test
/// gets a fresh, migrated schema in the shared Testcontainers container (isolation by schema name).
/// <para>
/// The source is constructed with <see cref="System.TimeSpan.Zero"/> visibility-lag so the safe-HWM
/// cutback is inert — the parity facts (ordering, bounds, limit, type-filter, fresh-visible) assume a
/// zero effective cutback, exactly like the single-process in-memory backend. The non-zero cutback is
/// exercised separately by <see cref="PostgresSubscriptionSourceHwmTests"/>.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresSubscriptionSourceContractTests(PostgresFixture fixture) : SubscriptionSourceContractTests
{
    private readonly PostgresFixture _fixture = fixture;

    protected override async ValueTask<(IEventStore Store, ISubscriptionSource Source)> CreateAsync()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, TestContext.Current.CancellationToken);
        var store = new PostgresEventStore(_fixture.DataSource, options);
        var source = new PostgresSubscriptionSource(_fixture.DataSource, options, TimeSpan.Zero);
        return (store, source);
    }
}
