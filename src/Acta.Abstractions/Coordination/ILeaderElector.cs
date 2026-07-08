namespace Acta.Abstractions;

/// <summary>
/// Elects a single leader per projection(×tenant) so that, across any multi-pod topology
/// (ADR-014), at most one pod owns the async work for a given (projection, tenant) slot at a time
/// (ADR-005, D14 — leadership per projection, not per node, so projections spread across pods).
/// <para>
/// <b>Contract.</b> <see cref="TryAcquireAsync"/> is <i>non-blocking</i>: it returns a held
/// <see cref="ILeadershipLease"/> when this pod won the slot, or <see langword="null"/> when another
/// pod already holds it (never waits for the incumbent to release). Leadership lives <b>only</b> in
/// the backend's coordination primitive — never in process memory, a file, or an env var (ADR-005,
/// "Zakazane granice"): a pod that loses the lease's underlying session loses leadership, and the
/// next <see cref="TryAcquireAsync"/> from any pod takes over from the last checkpoint (failover).
/// </para>
/// <para>
/// <b>Fencing boundary.</b> The elector fences <i>leadership</i> (one live leader at a time); the
/// checkpoint sink independently fences the <i>checkpoint write</i> with an <c>owner_token</c> CAS
/// (<c>ICheckpointSink.SaveAsync</c>, ADR-005 §fencing). The two together give split-brain safety:
/// even in the brief failover window a zombie leader's non-advancing write is rejected.
/// </para>
/// </summary>
public interface ILeaderElector
{
    /// <summary>
    /// Attempts to acquire leadership of the (<paramref name="projectionName"/>,
    /// <paramref name="tenantId"/>) slot without blocking. A <see langword="null"/>
    /// <paramref name="tenantId"/> denotes the single-tenant slot.
    /// </summary>
    /// <param name="projectionName">The projection whose leadership to acquire.</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    /// <param name="ct">A token to cancel the acquisition attempt.</param>
    /// <returns>
    /// A held <see cref="ILeadershipLease"/> when leadership was acquired; <see langword="null"/> when
    /// another holder already owns the slot. Dispose the lease to release leadership.
    /// </returns>
    ValueTask<ILeadershipLease?> TryAcquireAsync(string projectionName, string? tenantId, CancellationToken ct = default);
}
