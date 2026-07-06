using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Subscriptions.Contracts;

/// <summary>
/// Runs the shared <see cref="CheckpointSinkContractTests"/> against the in-memory backend. The
/// Postgres counterpart (<c>PostgresCheckpointSinkContractTests</c>) is added in Feature 7 and
/// derives from the same base with a Testcontainers fixture.
/// </summary>
public sealed class InMemoryCheckpointSinkContractTests : CheckpointSinkContractTests
{
    protected override ValueTask<ICheckpointSink> CreateSinkAsync()
        => ValueTask.FromResult<ICheckpointSink>(new InMemoryCheckpointSink());
}
