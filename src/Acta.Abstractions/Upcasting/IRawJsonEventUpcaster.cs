namespace Acta.Abstractions;

/// <summary>
/// A raw-JSON variant of <see cref="IEventUpcaster"/> that transforms the serialized payload
/// directly, without going through full CLR deserialization first — cheap for field-level schema
/// changes (rename/add/remove a JSON property) that do not need the fully-typed object graph.
/// Inherits the same contract as <see cref="IEventUpcaster"/> (no I/O, determinism,
/// convertibility per FR-8, cross-type and 1:N allowed), applied to raw
/// <see cref="ReadOnlyMemory{T}"/> JSON bytes instead of a deserialized CLR instance.
/// <para>
/// Homogeneity: a raw walk (<c>UpcasterChain.UpcastRaw</c>) only ever calls <see cref="UpcastRaw"/>
/// on the steps it visits — a step registered as a plain <see cref="IEventUpcaster"/> (not this
/// interface) encountered mid-walk is a configuration error there, not a supported mixed mode
/// (see <c>UpcasterChain.UpcastRaw</c>'s <see cref="InvalidOperationException"/> contract).
/// </para>
/// </summary>
public interface IRawJsonEventUpcaster : IEventUpcaster
{
    /// <summary>
    /// Converts a raw JSON payload (of <see cref="IEventUpcaster.EventType"/> at
    /// <see cref="IEventUpcaster.FromSchemaVersion"/>) to one or more target raw payloads. Must be
    /// pure, deterministic, and free of I/O — see <see cref="IEventUpcaster"/>'s contract.
    /// </summary>
    /// <param name="payloadJson">The source event's raw, serialized JSON payload.</param>
    /// <param name="metadata">The causation metadata associated with the source event.</param>
    /// <returns>The target raw payload(s) produced by this step (1:N fan-out is allowed).</returns>
    IReadOnlyList<RawUpcastedEvent> UpcastRaw(ReadOnlyMemory<byte> payloadJson, EventMetadata metadata);
}
