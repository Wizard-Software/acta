namespace Acta.Abstractions;

/// <summary>
/// An event read from the store (the output side of <c>IEventStore</c> reads).
/// </summary>
/// <param name="EventId">Unique identifier of the event.</param>
/// <param name="StreamId">Identifier of the stream this event belongs to.</param>
/// <param name="Version">
/// The event's position within its stream: 0-based, monotonic, and gapless.
/// </param>
/// <param name="GlobalPosition">
/// The event's position in the all-stream (bigint); gaps across streams are normal (ADR-001).
/// </param>
/// <param name="EventType">Logical, CLR-independent event name.</param>
/// <param name="SchemaVersion">Version of the payload schema, consumed by upcasting (FR-8).</param>
/// <param name="Payload">The serialized event payload (JSON via System.Text.Json — FR-10).</param>
/// <param name="Metadata">The causation metadata associated with this event.</param>
/// <param name="Timestamp">The wall-clock time at which the event was appended.</param>
public sealed record StoredEvent(
    Guid EventId,
    string StreamId,
    long Version,
    GlobalPosition GlobalPosition,
    string EventType,
    int SchemaVersion,
    ReadOnlyMemory<byte> Payload,
    EventMetadata Metadata,
    DateTimeOffset Timestamp);
