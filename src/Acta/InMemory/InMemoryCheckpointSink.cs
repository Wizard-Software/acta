using System.Collections.Concurrent;

using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local <see cref="ICheckpointSink"/> (Tier 1) — checkpoints held in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by (projection, tenant), with a
/// <see langword="null"/> tenant normalized to <c>""</c> (the single-tenant slot).
/// <para>
/// <b>"Checkpoint only advances":</b> <see cref="SaveAsync"/> accepts a higher position, is an
/// idempotent no-op on an equal one, and throws <see cref="InvalidOperationException"/> on a lower
/// one (no rollback outside an explicit rebuild — CONSTITUTION §2 FORBIDDEN, ADR-005). The
/// compare-and-throw runs <i>inside</i> the
/// <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate{TArg}(TKey, System.Func{TKey, TArg, TValue}, System.Func{TKey, TValue, TArg, TValue}, TArg)"/>
/// update delegate so it is atomic against concurrent writers (D10) — never a read-then-write
/// span, which would be a lost-update race able to silently roll the checkpoint back.
/// </para>
/// <para>
/// <b>Leadership fencing (D7):</b> the <c>ownerToken</c> compare-and-swap that yields
/// <see cref="CheckpointFencedException"/> is NOT enforced here — a single process has no leader
/// election or failover, hence no split-brain to fence. The token is validated for null/empty but
/// never compared. Fencing is the Postgres sink's responsibility (tasks 7.5/7.6).
/// </para>
/// </summary>
public sealed class InMemoryCheckpointSink : ICheckpointSink
{
    private readonly ConcurrentDictionary<CheckpointKey, GlobalPosition> _checkpoints = new();

    /// <inheritdoc/>
    public ValueTask<GlobalPosition?> LoadAsync(string projectionName, string? tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ct.ThrowIfCancellationRequested();

        var key = new CheckpointKey(projectionName, tenantId ?? "");
        GlobalPosition? result = _checkpoints.TryGetValue(key, out var position) ? position : null;
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc/>
    public ValueTask SaveAsync(string projectionName, string? tenantId, GlobalPosition position, string ownerToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        ct.ThrowIfCancellationRequested();

        var key = new CheckpointKey(projectionName, tenantId ?? "");

        // D10: the whole comparison branch (advance on '>', no-op on '==', throw on '<') runs
        // inside the atomic update delegate. A TryGetValue-then-write span would be a TOCTOU
        // lost-update race in which a lower position could overwrite a concurrently-advanced one.
        _checkpoints.AddOrUpdate(
            key,
            static (_, newPosition) => newPosition,
            static (existingKey, current, newPosition) =>
                newPosition < current
                    ? throw new InvalidOperationException(
                        $"Checkpoint for '{existingKey.ProjectionName}' cannot move backward from " +
                        $"{current.Value} to {newPosition.Value} (no rollback outside an explicit rebuild — ADR-005).")
                    : newPosition,
            position);

        return ValueTask.CompletedTask;
    }

    /// <summary>The checkpoint slot key: a projection scoped to a tenant (<c>null</c> tenant normalized to <c>""</c>).</summary>
    private readonly record struct CheckpointKey(string ProjectionName, string TenantId);
}
