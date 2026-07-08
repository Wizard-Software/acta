namespace Acta.Abstractions;

/// <summary>
/// Collects integration events raised while handling a command (ADR-002, D2 port 1/3), scoped to
/// that command's lifetime. Integration events collected here never travel the domain-event path
/// (<see cref="IEventAppendTransaction.AppendAsync"/>) — they take the separate integration-event
/// path (collector → outbox → relay, 03-contracts.md §4) and are enlisted into the current append
/// transaction by <see cref="IOutboxFlush"/>.
/// </summary>
public interface IIntegrationEventCollector
{
    /// <summary>Records <paramref name="integrationEvent"/> for later draining by <see cref="Drain"/>.</summary>
    /// <param name="integrationEvent">The integration event payload.</param>
    /// <param name="metadata">The causation metadata associated with the event.</param>
    void Collect(object integrationEvent, EventMetadata metadata);

    /// <summary>
    /// Returns every event collected so far, in collection order, and clears the internal
    /// buffer — a subsequent <see cref="Drain"/> call returns an empty list until
    /// <see cref="Collect"/> is called again.
    /// </summary>
    /// <returns>The drained integration events, in the order they were collected.</returns>
    IReadOnlyList<CollectedIntegrationEvent> Drain();
}
