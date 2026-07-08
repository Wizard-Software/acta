using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory stand-in for <see cref="IOutboxFlush"/> used to prove the AK-1 single-commit seam
/// (ADR-002) without a Postgres adapter (task 8.4): drains <paramref name="collector"/> and
/// enlists the drained integration events into the given transaction's buffer, so they are
/// published atomically with its appends when the transaction is committed.
/// <para>
/// Works only with <see cref="InMemoryEventAppendTransaction"/> — the same casting pattern a
/// Postgres adapter uses against its own transaction type; a foreign <see cref="IEventAppendTransaction"/>
/// implementation is rejected with <see cref="InvalidOperationException"/>. Like every
/// <see cref="IOutboxFlush"/> implementation, this stand-in never touches the network (ADR-002).
/// </para>
/// </summary>
/// <param name="collector">The collector drained on every <see cref="FlushAsync"/> call.</param>
public sealed class InMemoryOutboxFlush(IIntegrationEventCollector collector) : IOutboxFlush
{
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="tx"/> is not an <see cref="InMemoryEventAppendTransaction"/>.
    /// </exception>
    public ValueTask FlushAsync(IEventAppendTransaction tx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ct.ThrowIfCancellationRequested();

        if (tx is not InMemoryEventAppendTransaction inMemoryTx)
        {
            throw new InvalidOperationException(
                $"InMemoryOutboxFlush requires an InMemoryEventAppendTransaction, but received {tx.GetType().Name}.");
        }

        inMemoryTx.EnlistOutbox(collector.Drain());
        return ValueTask.CompletedTask;
    }
}
