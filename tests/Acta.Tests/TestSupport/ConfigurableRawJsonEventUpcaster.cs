using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// A configurable <see cref="IRawJsonEventUpcaster"/> test double (task 8.1), mirroring
/// <see cref="ConfigurableEventUpcaster"/> for the raw-JSON walk: <see cref="TransformRaw"/> fully
/// controls what <see cref="UpcastRaw"/> returns. The object-walk <see cref="Upcast"/> member
/// (required by <see cref="IEventUpcaster"/>) is not expected to be exercised by raw-walk tests and
/// throws if called.
/// </summary>
public sealed class ConfigurableRawJsonEventUpcaster : IRawJsonEventUpcaster
{
    /// <inheritdoc/>
    public required string EventType { get; init; }

    /// <inheritdoc/>
    public required int FromSchemaVersion { get; init; }

    /// <summary>Fully controls the result of <see cref="UpcastRaw"/> for a given input.</summary>
    public required Func<ReadOnlyMemory<byte>, EventMetadata, IReadOnlyList<RawUpcastedEvent>> TransformRaw { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<RawUpcastedEvent> UpcastRaw(ReadOnlyMemory<byte> payloadJson, EventMetadata metadata) => TransformRaw(payloadJson, metadata);

    /// <inheritdoc/>
    public IReadOnlyList<UpcastedEvent> Upcast(object @event, EventMetadata metadata) =>
        throw new NotSupportedException(
            $"{nameof(ConfigurableRawJsonEventUpcaster)} is a raw-JSON-only test double; it does not support the object walk.");
}
