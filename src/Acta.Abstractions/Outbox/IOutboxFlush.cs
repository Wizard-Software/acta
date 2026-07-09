namespace Acta.Abstractions;

/// <summary>
/// Enlists collected integration events into the current append transaction (ADR-002, D2 port
/// 2/3). A Postgres adapter implements this port by inserting into the outbox table within
/// <paramref name="tx"/>'s underlying database transaction; the core ships an in-memory stand-in
/// used only to prove the single-commit seam (AK-1). This is NOT publication — the entries become
/// visible only when <paramref name="tx"/> is committed, and delivering them to a broker is the
/// separate responsibility of <see cref="IIntegrationEventPublisher"/>. Implementations MUST NOT
/// perform any network I/O.
/// </summary>
public interface IOutboxFlush
{
    /// <summary>
    /// Drains the collector and enlists the collected integration events into
    /// <paramref name="tx"/>, so they are published atomically together with its appends when
    /// <see cref="IEventAppendTransaction.CommitAsync"/> is called.
    /// </summary>
    /// <param name="tx">The append transaction to enlist the outbox entries into.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask FlushAsync(IEventAppendTransaction tx, CancellationToken ct = default);
}
