namespace Acta.Abstractions;

/// <summary>
/// Set-based uniqueness reservation store (FR-16, ADR-009): the durable guardian of a single-value
/// uniqueness guarantee (e.g. a unique e-mail or login) across a multi-pod topology. The guarantee is
/// enforced <b>exclusively</b> by a database uniqueness constraint — never by application logic or an
/// in-process cache — so it survives a restart and is visible to every pod over the shared store.
/// <para>
/// <b>Two-phase reservation.</b> A command first <see cref="TryReserveAsync"/>s the value for a short
/// TTL, does its work, then either <see cref="ConfirmAsync"/>s it (making the reservation permanent)
/// or <see cref="ReleaseAsync"/>s it (compensating a failure). An unconfirmed reservation whose TTL
/// has elapsed may be lazily taken over by a different owner — a crashed command never wedges a value
/// forever.
/// </para>
/// <para>
/// <b>Per-tenant isolation (ADR-016).</b> Every operation is scoped by <c>tenantId</c>: the same value
/// may be reserved concurrently by different tenants without collision. A <see langword="null"/>
/// <c>tenantId</c> denotes the single-tenant slot.
/// </para>
/// <para>
/// <b>No PII in diagnostics (ADR-008, 06-cross-cutting §3.2).</b> Implementations MUST NOT log the
/// reservation <c>scope</c>, <c>value</c>, or <c>ownerId</c> — the value is classified PII (an
/// e-mail/login). Only row counts / boolean outcomes and the tenant scope key may be logged.
/// </para>
/// </summary>
public interface IReservationStore
{
    /// <summary>
    /// Attempts to reserve <paramref name="value"/> within <paramref name="scope"/> for
    /// <paramref name="ownerId"/>, holding the reservation unconfirmed for at most
    /// <paramref name="ttl"/>. Returns <see langword="true"/> when the reservation is granted — the
    /// value was free, or an <i>unconfirmed and expired</i> reservation of the same value was lazily
    /// taken over — and <see langword="false"/> when an active or confirmed reservation of that value
    /// already exists within the tenant. A collision is a <see langword="false"/> result, never an
    /// exception (ADR-009).
    /// </summary>
    /// <param name="scope">The uniqueness scope (e.g. <c>"email"</c>).</param>
    /// <param name="value">The value whose uniqueness is reserved (PII — never logged).</param>
    /// <param name="ownerId">The reservation owner (the command/aggregate acquiring it).</param>
    /// <param name="ttl">How long the unconfirmed reservation lives before it may be taken over.</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> if reserved; <see langword="false"/> on collision.</returns>
    ValueTask<bool> TryReserveAsync(string scope, string value, string ownerId, TimeSpan ttl, string? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Confirms <paramref name="ownerId"/>'s own unconfirmed reservation of <paramref name="value"/>
    /// within <paramref name="scope"/>, making it permanent (its TTL is cleared). Idempotent: a second
    /// confirm is a no-op. Guarded by owner — if the reservation was already taken over by a different
    /// owner after its TTL elapsed, this is a silent no-op (the reservation belongs to the new owner);
    /// it never throws (ADR-009).
    /// </summary>
    /// <param name="scope">The uniqueness scope.</param>
    /// <param name="value">The reserved value (PII — never logged).</param>
    /// <param name="ownerId">The owner confirming its reservation.</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask ConfirmAsync(string scope, string value, string ownerId, string? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Releases <paramref name="ownerId"/>'s own <i>unconfirmed</i> reservation of
    /// <paramref name="value"/> within <paramref name="scope"/> — the compensation for a failed
    /// command. A confirmed reservation is permanent and is never released (no-op), and a reservation
    /// owned by someone else is left untouched (no-op). Never throws (ADR-009).
    /// </summary>
    /// <param name="scope">The uniqueness scope.</param>
    /// <param name="value">The reserved value (PII — never logged).</param>
    /// <param name="ownerId">The owner releasing its reservation.</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask ReleaseAsync(string scope, string value, string ownerId, string? tenantId = null, CancellationToken ct = default);
}
