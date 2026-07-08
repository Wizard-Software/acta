namespace Acta.Abstractions;

/// <summary>
/// The result of one <see cref="IRawJsonEventUpcaster.UpcastRaw"/> step: a target raw JSON
/// payload together with the logical event type and schema version it now carries. Mirrors
/// <see cref="UpcastedEvent"/> for the raw-JSON walk — see <see cref="UpcastedEvent"/> for the
/// cross-type / 1:N walk-key semantics, which apply identically here.
/// </summary>
/// <param name="PayloadJson">The target event's raw, serialized JSON payload.</param>
/// <param name="EventType">The target event's logical, CLR-independent event-type name.</param>
/// <param name="SchemaVersion">The target event's payload schema version.</param>
public sealed record RawUpcastedEvent(ReadOnlyMemory<byte> PayloadJson, string EventType, int SchemaVersion);
