using System.Collections.Concurrent;

using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local <see cref="ILeaderElector"/> (Tier 1) — the single-process parity of
/// <c>AdvisoryLockLeaderElector</c>. A single process has no cross-pod election (ADR-014, D14), but
/// leadership per (projection, tenant) is still <b>single-active</b> <i>within</i> the process: the
/// first <see cref="TryAcquireAsync"/> for a slot wins, a concurrent second one for the same slot
/// returns <see langword="null"/> until the first lease is disposed.
/// <para>
/// Slots are tracked in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by the
/// <c>(projection, tenant)</c> value tuple — a <see langword="null"/> tenant normalized to <c>""</c>
/// (the single-tenant slot), with no string delimiter so no two distinct pairs can ever collide.
/// Acquisition is a single atomic <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> (no
/// read-then-write race), and release a single <c>TryRemove</c>.
/// </para>
/// <para>
/// There is no session to lose in a single process, so a held lease's <see cref="ILeadershipLease.IsHeldAsync"/>
/// stays <see langword="true"/> until it is disposed — the in-memory backend never fails over
/// (ADR-005: "In-memory — single-process without election"). The PostgreSQL backend replaces this
/// registration with the advisory-lock elector, whose session loss <i>is</i> a failover.
/// </para>
/// </summary>
public sealed class InMemoryLeaderElector : ILeaderElector
{
    // value is unused; the dictionary is a concurrent set of currently-held (projection, tenant) slots.
    private readonly ConcurrentDictionary<(string Projection, string Tenant), byte> _held = new();

    /// <inheritdoc/>
    public ValueTask<ILeadershipLease?> TryAcquireAsync(string projectionName, string? tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ct.ThrowIfCancellationRequested();

        var key = (projectionName, tenantId ?? string.Empty);
        if (!_held.TryAdd(key, 0))
        {
            // Another lease already owns this slot in-process.
            return ValueTask.FromResult<ILeadershipLease?>(null);
        }

        return ValueTask.FromResult<ILeadershipLease?>(new InMemoryLease(_held, key, projectionName, tenantId));
    }

    private sealed class InMemoryLease(
        ConcurrentDictionary<(string Projection, string Tenant), byte> held,
        (string Projection, string Tenant) key,
        string projectionName,
        string? tenantId) : ILeadershipLease
    {
        private readonly ConcurrentDictionary<(string Projection, string Tenant), byte> _held = held;
        private readonly (string Projection, string Tenant) _key = key;
        private int _disposed;

        public string ProjectionName { get; } = projectionName;

        public string? TenantId { get; } = tenantId;

        public ValueTask<bool> IsHeldAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // No session to lose in-process: held until disposed.
            return ValueTask.FromResult(Volatile.Read(ref _disposed) == 0);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _held.TryRemove(_key, out _);
            }

            return ValueTask.CompletedTask;
        }
    }
}
