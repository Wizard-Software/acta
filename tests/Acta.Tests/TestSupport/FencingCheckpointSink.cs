using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// An <see cref="ICheckpointSink"/> test double that always fences (task 5.2, fence handling test):
/// <see cref="SaveAsync"/> throws <see cref="CheckpointFencedException"/> on every call, and
/// <see cref="LoadAsync"/> returns <see langword="null"/> while counting its invocations — so a test
/// can assert the daemon reloads the checkpoint on the next tick after dropping leadership. The
/// in-memory sink never fences (D8); this double exercises the daemon's zombie-guard path for
/// Postgres readiness.
/// </summary>
public sealed class FencingCheckpointSink : ICheckpointSink
{
    /// <summary>The number of times <see cref="LoadAsync"/> has been called.</summary>
    public int LoadCallCount { get; private set; }

    /// <summary>The number of times <see cref="SaveAsync"/> has been called (each of which threw).</summary>
    public int SaveCallCount { get; private set; }

    /// <inheritdoc/>
    public ValueTask<GlobalPosition?> LoadAsync(string projectionName, string? tenantId, CancellationToken ct = default)
    {
        LoadCallCount++;
        return ValueTask.FromResult<GlobalPosition?>(null);
    }

    /// <inheritdoc/>
    public ValueTask SaveAsync(string projectionName, string? tenantId, GlobalPosition position, string ownerToken, CancellationToken ct = default)
    {
        SaveCallCount++;
        throw new CheckpointFencedException(projectionName, ownerToken);
    }
}
