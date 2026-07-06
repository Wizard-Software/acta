using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Subscriptions.Contracts;

/// <summary>
/// Runs the shared <see cref="SubscriptionSourceContractTests"/> against the in-memory backend. The
/// Postgres counterpart (<c>PostgresSubscriptionSourceContractTests</c>) is added in Feature 7 and
/// derives from the same base with a Testcontainers fixture.
/// </summary>
public sealed class InMemorySubscriptionSourceContractTests : SubscriptionSourceContractTests
{
    protected override ValueTask<(IEventStore Store, ISubscriptionSource Source)> CreateAsync()
    {
        var store = new InMemoryEventStore();
        return ValueTask.FromResult<(IEventStore, ISubscriptionSource)>((store, new InMemorySubscriptionSource(store)));
    }
}
