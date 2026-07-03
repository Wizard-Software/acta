namespace Acta.Abstractions;

/// <summary>
/// An event after deserialization and upcasting — the consumer-facing side of event
/// processing.
/// </summary>
/// <param name="Event">The CLR instance obtained after deserialization and upcasting.</param>
/// <param name="Raw">The underlying <see cref="StoredEvent"/>, giving access to the raw stored data and metadata.</param>
public sealed record SourcedEvent(
    object Event,
    StoredEvent Raw);
