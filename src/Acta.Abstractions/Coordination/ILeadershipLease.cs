namespace Acta.Abstractions;

/// <summary>
/// A held leadership grant for one (projection, tenant) slot, returned by
/// <see cref="ILeaderElector.TryAcquireAsync"/>. Leadership is held for as long as the lease is
/// alive; <see cref="System.IAsyncDisposable.DisposeAsync"/> releases it (a clean hand-off), and an
/// abrupt loss of the lease's underlying session releases it too (failover — ADR-005).
/// </summary>
public interface ILeadershipLease : IAsyncDisposable
{
    /// <summary>The projection this lease grants leadership of.</summary>
    string ProjectionName { get; }

    /// <summary>The tenant scope of this lease, or <see langword="null"/> for the single-tenant slot.</summary>
    string? TenantId { get; }

    /// <summary>
    /// Reports whether leadership is still held — <see langword="true"/> while the lease is alive and
    /// its backing session healthy, <see langword="false"/> once the lease was disposed or its session
    /// was lost (the failover signal the daemon polls before each fenced checkpoint write). Never
    /// throws for a lost session; only propagates cancellation of <paramref name="ct"/>.
    /// </summary>
    /// <param name="ct">A token to cancel the liveness probe.</param>
    /// <returns><see langword="true"/> if leadership is still held; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> IsHeldAsync(CancellationToken ct = default);
}
