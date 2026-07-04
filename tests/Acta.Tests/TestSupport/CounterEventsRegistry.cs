using System.Text.Json;

using Acta.Abstractions;
using Acta.Aggregates;
using Acta.InMemory;
using Acta.Serialization;

namespace Acta.Tests.TestSupport;

/// <summary>
/// Test kit for <c>AggregateRepositoryTests</c> (task 3.2): registers <see cref="CounterAggregate"/>'s
/// event types (<see cref="Incremented"/>/<see cref="Decremented"/>/<see cref="UnknownEvent"/>) in an
/// <see cref="EventTypeRegistry"/>, builds an <see cref="EventSerializer"/> over it, and supplies a
/// stable <see cref="EventMetadata"/> factory plus a one-line <see cref="AggregateRepository{TAggregate}"/>
/// builder over an <see cref="InMemoryEventStore"/>.
/// </summary>
public static class CounterEventsRegistry
{
    /// <summary>
    /// Builds a fresh <see cref="EventTypeRegistry"/> with <see cref="CounterAggregate"/>'s three
    /// event types registered — including <see cref="UnknownEvent"/>, which is registered (so the
    /// serializer's allow-list accepts it) but not recognized by <see cref="CounterAggregate.Apply"/>,
    /// modeling "registered event, no longer/not yet meaningful to this aggregate" (FR-11, AK-4).
    /// </summary>
    /// <returns>A new registry with the three event types registered.</returns>
    public static EventTypeRegistry CreateRegistry() =>
        new EventTypeRegistry()
            .Register<Incremented>()
            .Register<Decremented>()
            .Register<UnknownEvent>();

    /// <summary>Builds an <see cref="EventSerializer"/> over a fresh <see cref="CreateRegistry"/> and default JSON options.</summary>
    /// <returns>A new serializer bound to a fresh registry.</returns>
    public static EventSerializer CreateSerializer() => new(CreateRegistry(), JsonSerializerOptions.Default);

    /// <summary>
    /// Builds a fixed <see cref="EventMetadata"/> factory: every invocation of the RETURNED
    /// delegate yields the exact same <see cref="EventMetadata"/> instance (same <c>MessageId</c>/
    /// <c>CorrelationId</c>/<c>CausationId</c>), modeling "the same command" across however many
    /// times it is retried. Two separate calls to <see cref="FixedMetadataFactory"/> itself produce
    /// two independent fixed factories (fresh <see cref="Guid"/>s each), modeling two distinct
    /// commands — this is the shape idempotency/retry tests need: reuse ONE factory (and the
    /// repository built from it) to simulate a retry; build a second one to simulate a genuinely
    /// new command against the same stream.
    /// </summary>
    /// <returns>A delegate that always returns the same <see cref="EventMetadata"/> instance.</returns>
    public static Func<EventMetadata> FixedMetadataFactory()
    {
        var metadata = new EventMetadata
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            CausationId = Guid.NewGuid(),
        };
        return () => metadata;
    }

    /// <summary>
    /// Convenience: builds an <see cref="AggregateRepository{TAggregate}"/> for
    /// <see cref="CounterAggregate"/>, sharing a fresh <see cref="CreateSerializer"/> and, unless
    /// overridden, a fresh <see cref="FixedMetadataFactory"/>.
    /// </summary>
    /// <param name="store">
    /// The store the repository reads from and appends to; defaults to a fresh
    /// <see cref="InMemoryEventStore"/> when omitted. Pass an existing store to build a second
    /// repository (typically with its own <paramref name="metadataFactory"/>) over the same
    /// underlying data — e.g. to model a second, distinct command against a stream already
    /// written by a first repository instance.
    /// </param>
    /// <param name="metadataFactory">
    /// The metadata factory to use; defaults to a fresh <see cref="FixedMetadataFactory"/> when omitted.
    /// </param>
    /// <returns>A new repository ready to use against <paramref name="store"/>.</returns>
    public static AggregateRepository<CounterAggregate> CreateRepository(
        IEventStore? store = null,
        Func<EventMetadata>? metadataFactory = null) =>
        new(store ?? new InMemoryEventStore(), CreateSerializer(), metadataFactory ?? FixedMetadataFactory());
}
