namespace Acta.Abstractions;

/// <summary>
/// Command idempotency store (FR-7, ADR-003): the durable dedup of command <i>entry</i> across a
/// multi-pod topology. A command registers its idempotency key once; a retry of the same command
/// (same key) is recognized and the previously-computed result is returned instead of re-executing.
/// The guarantee is enforced <b>exclusively</b> by a database primary key — never by an in-process
/// cache — so it survives a restart and is honored by every pod over the shared store.
/// <para>
/// <b>Register / save / get.</b> A command first <see cref="TryRegisterAsync"/>s its key for a
/// retention window: <see langword="true"/> means "first execution — proceed", <see langword="false"/>
/// means "duplicate — return the remembered result". After executing, the command
/// <see cref="SaveResultAsync"/>s its result; a later duplicate reads it via
/// <see cref="GetResultAsync"/>. An entry whose retention has elapsed may be lazily re-registered
/// (the command becomes executable again).
/// </para>
/// <para>
/// <b>Per-tenant isolation (ADR-016).</b> Every operation is scoped by <c>tenantId</c>: the same
/// idempotency key is independent across tenants. A <see langword="null"/> <c>tenantId</c> denotes the
/// single-tenant slot.
/// </para>
/// <para>
/// <b>No PII in diagnostics (ADR-008, 06-cross-cutting §3.2).</b> Implementations MUST NOT log the
/// <c>idempotencyKey</c> or the <c>result</c> bytes (the result may embed command output classified
/// PII); only row counts / boolean outcomes and the tenant scope key may be logged.
/// </para>
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Registers <paramref name="idempotencyKey"/> for a <paramref name="retention"/> window. Returns
    /// <see langword="true"/> when this is the first execution — the key was new, or an <i>expired</i>
    /// entry was lazily taken over — and <see langword="false"/> when an active registration of the
    /// same key already exists (a duplicate: the caller should return the remembered result from
    /// <see cref="GetResultAsync"/>). Never throws on a duplicate (ADR-003).
    /// </summary>
    /// <param name="idempotencyKey">The command's idempotency key (never logged).</param>
    /// <param name="retention">How long the registration is honored before it may be re-registered.</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> to execute the command; <see langword="false"/> for a duplicate.</returns>
    ValueTask<bool> TryRegisterAsync(string idempotencyKey, TimeSpan retention, string? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the result previously saved for <paramref name="idempotencyKey"/>, or
    /// <see langword="null"/> when no result has been saved yet (the key is unknown, or it is
    /// registered but its command has not yet called <see cref="SaveResultAsync"/>).
    /// </summary>
    /// <param name="idempotencyKey">The command's idempotency key (never logged).</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The remembered result bytes, or <see langword="null"/> if none.</returns>
    ValueTask<ReadOnlyMemory<byte>?> GetResultAsync(string idempotencyKey, string? tenantId = null, CancellationToken ct = default);

    /// <summary>
    /// Saves <paramref name="result"/> as the remembered result for <paramref name="idempotencyKey"/>,
    /// so a later duplicate reads it via <see cref="GetResultAsync"/>. Intended to be called once by
    /// the command that <see cref="TryRegisterAsync"/>ed the key.
    /// </summary>
    /// <param name="idempotencyKey">The command's idempotency key (never logged).</param>
    /// <param name="result">The command result bytes to remember (never logged).</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask SaveResultAsync(string idempotencyKey, ReadOnlyMemory<byte> result, string? tenantId = null, CancellationToken ct = default);
}
