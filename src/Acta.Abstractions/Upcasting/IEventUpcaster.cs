namespace Acta.Abstractions;

/// <summary>
/// A single step in an upcaster chain (<c>UpcasterChain</c>, Acta core), keyed by
/// <see cref="EventType"/> and <see cref="FromSchemaVersion"/>. Upcasting is the on-read
/// compensation mechanism for evolving event schemas (FR-8): the event journal is append-only, so
/// an older event's stored shape is never rewritten in place — instead, every read re-derives the
/// current shape by walking the chain of registered upcasters forward from the stored schema
/// version.
/// <para>
/// Contract:
/// <list type="bullet">
/// <item><description><b>No I/O.</b> <see cref="Upcast"/> runs on every deserialization
/// (potentially the hot read path) and MUST be a pure, deterministic, in-memory transformation —
/// no network calls, no database access, no file access, no blocking waits. This is a documented
/// contract enforced by code review, not by an automated guard (CONSTITUTION FORBIDDEN list).
/// </description></item>
/// <item><description><b>Convertibility (FR-8).</b> The target event(s) MUST be derivable from
/// <paramref name="event"/> and <paramref name="metadata"/> alone: an upcaster is a pure function
/// of its inputs and never consults external state.</description></item>
/// <item><description><b>Cross-type allowed.</b> A step MAY change
/// <see cref="UpcastedEvent.EventType"/> to a different logical event type than
/// <see cref="EventType"/> — the walk continues keyed on the new type.</description></item>
/// <item><description><b>1:N (fan-out) allowed.</b> <see cref="Upcast"/> MAY return more than one
/// event (à la Axon's <c>EventMultiUpcaster</c>) — e.g. splitting one legacy event into several
/// current ones. Each returned event continues the walk independently.</description></item>
/// </list>
/// </para>
/// </summary>
public interface IEventUpcaster
{
    /// <summary>The logical event-type name this upcaster accepts (part of the walk lookup key).</summary>
    string EventType { get; }

    /// <summary>
    /// The source schema version this upcaster accepts (part of the walk lookup key, together
    /// with <see cref="EventType"/>).
    /// </summary>
    int FromSchemaVersion { get; }

    /// <summary>
    /// Converts <paramref name="event"/> (of <see cref="EventType"/> at
    /// <see cref="FromSchemaVersion"/>) to one or more target events. Must be pure, deterministic,
    /// and free of I/O — see the interface-level contract.
    /// </summary>
    /// <param name="event">The source event instance.</param>
    /// <param name="metadata">The causation metadata associated with the source event.</param>
    /// <returns>The target event(s) produced by this step (1:N fan-out is allowed).</returns>
    IReadOnlyList<UpcastedEvent> Upcast(object @event, EventMetadata metadata);
}
