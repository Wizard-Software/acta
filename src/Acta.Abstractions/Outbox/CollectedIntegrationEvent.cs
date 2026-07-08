namespace Acta.Abstractions;

/// <summary>
/// An integration event paired with its causation metadata, as drained from an
/// <see cref="IIntegrationEventCollector"/> and enlisted into an
/// <see cref="IEventAppendTransaction"/> by <see cref="IOutboxFlush"/>.
/// </summary>
/// <param name="Event">The integration event payload.</param>
/// <param name="Metadata">The causation metadata associated with the event.</param>
public sealed record CollectedIntegrationEvent(object Event, EventMetadata Metadata);
