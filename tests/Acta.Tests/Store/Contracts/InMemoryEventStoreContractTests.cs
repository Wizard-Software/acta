using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Store.Contracts;

/// <summary>
/// Runs the shared <see cref="EventStoreContractTests"/> against the in-memory backend. The
/// Postgres counterpart (<c>PostgresEventStoreContractTests</c>) is added in task 7.2 and derives
/// from the same base with a Testcontainers fixture.
/// </summary>
public sealed class InMemoryEventStoreContractTests : EventStoreContractTests
{
    protected override ValueTask<IEventStore> CreateStoreAsync()
        => ValueTask.FromResult<IEventStore>(new InMemoryEventStore());
}
