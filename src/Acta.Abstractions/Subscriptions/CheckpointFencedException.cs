namespace Acta.Abstractions;

/// <summary>
/// Thrown when a checkpoint compare-and-swap fails because the writer lost leadership (fencing,
/// D5 / ADR-005): another owner has taken over the projection, so this
/// <see cref="ICheckpointSink.SaveAsync"/> is rejected and the daemon must stop the projection.
/// Enforced by the Postgres sink (tasks 7.5/7.6); the single-process in-memory sink never elects a
/// leader and so never throws this — the type exists now for cross-backend contract completeness.
/// </summary>
/// <param name="projectionName">The projection whose checkpoint save was fenced.</param>
/// <param name="ownerToken">The (now stale) owner token that attempted the write.</param>
public sealed class CheckpointFencedException(string projectionName, string ownerToken)
    : Exception($"Checkpoint CAS failed for '{projectionName}' (owner '{ownerToken}') — leadership lost")
{
    /// <summary>The projection whose checkpoint save was fenced.</summary>
    public string ProjectionName { get; } = projectionName;

    /// <summary>The (now stale) owner token that attempted the write.</summary>
    public string OwnerToken { get; } = ownerToken;
}
