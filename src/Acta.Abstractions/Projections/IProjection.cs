namespace Acta.Abstractions;

/// <summary>
/// A typed projection for a single event type — Tier 2's primary read-model update seam
/// (FR-5/FR-6, MODULE-INTERFACES Grupa 5).
/// </summary>
/// <typeparam name="TEvent">
/// The event type this projection handles. <c>in</c> (contravariant): dispatch happens by runtime
/// CLR type, so a projection declared for a base event type also matches — and receives —
/// instances of any derived event type.
/// </typeparam>
/// <remarks>
/// Enforcement (ADR-005): <see cref="ApplyAsync"/> MUST be idempotent. Two independent situations
/// require this: a full rebuild-from-0 replays every event from the beginning of the stream, so
/// any event already applied during a previous rebuild is applied again; and the at-least-once
/// failover window inherent to any inline or async runner can redeliver the same event after a
/// crash/restart, before the runner's own bookkeeping (e.g. a checkpoint or watermark) has
/// advanced past it. Applying the same event twice MUST leave this projection's read model in
/// exactly the same state as applying it once.
/// </remarks>
public interface IProjection<in TEvent>
{
    /// <summary>
    /// Applies <paramref name="event"/> to this projection's read model. MUST be idempotent — see
    /// the interface remarks (ADR-005): both rebuild-from-0 replay and the at-least-once failover
    /// window can redeliver an already-applied event.
    /// </summary>
    /// <param name="event">The deserialized (and, once upcasting exists, upcasted) event instance.</param>
    /// <param name="raw">The underlying <see cref="StoredEvent"/> the event was read from.</param>
    /// <param name="ct">A token to observe for cancellation.</param>
    ValueTask ApplyAsync(TEvent @event, StoredEvent raw, CancellationToken ct = default);
}
