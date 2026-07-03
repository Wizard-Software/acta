namespace Acta.Abstractions;

/// <summary>
/// An event to be appended to a stream (the input side of <c>IEventStore.AppendAsync</c>).
/// Append-time deduplication is keyed on <c>(streamId, EventId)</c> — see ADR-003.
/// </summary>
/// <param name="EventId">Deduplication key for the append, combined with the target stream id.</param>
/// <param name="EventType">Logical, CLR-independent event name, conventionally in the past tense.</param>
/// <param name="SchemaVersion">Version of the payload schema, consumed by upcasting (FR-8).</param>
/// <param name="Payload">The serialized event payload (JSON via System.Text.Json — FR-10).</param>
/// <param name="Metadata">The causation metadata associated with this event.</param>
public sealed record EventData(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    ReadOnlyMemory<byte> Payload,
    EventMetadata Metadata);
