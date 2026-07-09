using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Idempotency.Contracts;

/// <summary>
/// Runs the shared <see cref="IdempotencyStoreContractTests"/> suite (task 8.5) against the in-memory
/// backend: the exact same facts that constrain <c>PostgresIdempotencyStore</c> constrain
/// <see cref="InMemoryIdempotencyStore"/>.
/// </summary>
public sealed class InMemoryIdempotencyStoreContractTests : IdempotencyStoreContractTests
{
    protected override ValueTask<IIdempotencyStore> CreateStoreAsync()
        => ValueTask.FromResult<IIdempotencyStore>(new InMemoryIdempotencyStore());
}
