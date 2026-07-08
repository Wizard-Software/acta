namespace Acta.Abstractions;

/// <summary>
/// Factory for beginning a new <see cref="IEventAppendTransaction"/> — the unit of atomicity for
/// the "append + outbox" single-commit seam (ADR-002, FR-14). A backend implements this by
/// opening a genuine database transaction (the Postgres adapter) or, in the core, a buffered
/// in-memory transaction used only to prove the single-commit seam (AK-1).
/// </summary>
public interface IEventAppendTransactionFactory
{
    /// <summary>
    /// Begins a new <see cref="IEventAppendTransaction"/>. The caller owns its lifetime: it MUST
    /// either call <see cref="IEventAppendTransaction.CommitAsync"/> or dispose it without
    /// committing — an implicit rollback via <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A new, uncommitted <see cref="IEventAppendTransaction"/>.</returns>
    ValueTask<IEventAppendTransaction> BeginAsync(CancellationToken ct = default);
}
