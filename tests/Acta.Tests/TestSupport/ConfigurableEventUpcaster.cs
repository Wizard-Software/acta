using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// A configurable <see cref="IEventUpcaster"/> test double (task 8.1): <see cref="EventType"/> and
/// <see cref="FromSchemaVersion"/> are settable (the chain's walk lookup key), and
/// <see cref="Transform"/> fully controls what <see cref="Upcast"/> returns for a given input —
/// including fan-out (return more than one element), cross-type (return a different
/// <c>EventType</c>), or an unconditional self-loop (ignore the input and always return this
/// upcaster's own key, regardless of what is passed in).
/// </summary>
public sealed class ConfigurableEventUpcaster : IEventUpcaster
{
    /// <inheritdoc/>
    public required string EventType { get; init; }

    /// <inheritdoc/>
    public required int FromSchemaVersion { get; init; }

    /// <summary>Fully controls the result of <see cref="Upcast"/> for a given input.</summary>
    public required Func<object, EventMetadata, IReadOnlyList<UpcastedEvent>> Transform { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<UpcastedEvent> Upcast(object @event, EventMetadata metadata) => Transform(@event, metadata);
}
