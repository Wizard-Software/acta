namespace Acta.Abstractions;

/// <summary>
/// Relays pending outbox entries to a broker (ADR-002, D2 port 3/3) — purely adapter-side; the
/// core does not ship an implementation (see the roadmap). Multi-pod topologies default to a
/// single-active relay (e.g. an advisory lock), with an optional competing-consumers mode (e.g.
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c>) that only preserves ordering per stream/partition.
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>Publishes up to <paramref name="batchSize"/> pending outbox entries.</summary>
    /// <param name="batchSize">The maximum number of entries to publish in this call.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask PublishPendingAsync(int batchSize, CancellationToken ct = default);
}
