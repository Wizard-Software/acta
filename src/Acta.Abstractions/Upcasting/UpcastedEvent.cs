namespace Acta.Abstractions;

/// <summary>
/// The result of one <see cref="IEventUpcaster.Upcast"/> step: a target event instance together
/// with the logical event type and schema version it now carries.
/// <para>
/// <c>UpcasterChain</c> (Acta core) uses <see cref="EventType"/>/<see cref="SchemaVersion"/> as
/// the key for the next hop of the walk, so a cross-type conversion (a different
/// <see cref="EventType"/> than the source event's) is a normal, supported outcome — not an
/// error. A single upcaster step MAY also fan out to more than one <see cref="UpcastedEvent"/>
/// (1:N, à la Axon's <c>EventMultiUpcaster</c>); each one then continues the walk independently.
/// </para>
/// </summary>
/// <param name="Event">The target event instance.</param>
/// <param name="EventType">The target event's logical, CLR-independent event-type name.</param>
/// <param name="SchemaVersion">The target event's payload schema version.</param>
public sealed record UpcastedEvent(object Event, string EventType, int SchemaVersion);
