namespace Acta.Abstractions;

/// <summary>
/// Sink for projection checkpoints — the durable record of how far a projection has consumed the
/// all-stream. Persisting progress lets a projection resume after a restart instead of rebuilding
/// from the beginning.
/// <para>
/// ADR-015 / ADR-005: <see cref="SaveAsync"/> MUST be a compare-and-swap fenced by
/// <c>ownerToken</c> (leadership fencing, D5) — a failed CAS throws
/// <see cref="CheckpointFencedException"/> and the daemon stops that projection. A checkpoint only
/// ever moves <i>forward</i>; a save that would move it backward is forbidden outside an explicit
/// rebuild (no silent rollback).
/// </para>
/// </summary>
public interface ICheckpointSink
{
    /// <summary>
    /// Loads the last saved position for (<paramref name="projectionName"/>,
    /// <paramref name="tenantId"/>), or <see langword="null"/> when the projection has no
    /// checkpoint yet. A <see langword="null"/> <paramref name="tenantId"/> denotes the
    /// single-tenant slot.
    /// </summary>
    /// <param name="projectionName">The projection whose checkpoint to load.</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The saved <see cref="GlobalPosition"/>, or <see langword="null"/> if none exists.</returns>
    ValueTask<GlobalPosition?> LoadAsync(string projectionName, string? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Persists <paramref name="position"/> as the checkpoint for (<paramref name="projectionName"/>,
    /// <paramref name="tenantId"/>), fenced by <paramref name="ownerToken"/>. The checkpoint only
    /// advances: saving a position lower than the current one is forbidden (no rollback outside an
    /// explicit rebuild, ADR-005) and saving the same position is an idempotent no-op. A failed
    /// leadership CAS throws <see cref="CheckpointFencedException"/>.
    /// </summary>
    /// <param name="projectionName">The projection whose checkpoint to save.</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    /// <param name="position">The position consumed so far; must not move the checkpoint backward.</param>
    /// <param name="ownerToken">The leadership token fencing this write (compare-and-swap).</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <exception cref="CheckpointFencedException">The leadership CAS failed — ownership was lost.</exception>
    ValueTask SaveAsync(string projectionName, string? tenantId, GlobalPosition position, string ownerToken, CancellationToken ct = default);
}
