using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.InMemory;
using Acta.Projections.Daemon;
using Acta.Projections.Inline;
using Acta.Serialization;

namespace Acta.Tests.TestSupport;

/// <summary>
/// Shared harness for the <see cref="ProjectionDaemon"/> / <see cref="HwmPoller"/> unit tests (task
/// 5.2): a fresh in-memory store + checkpoint sink, a serializer/registry for
/// <see cref="Incremented"/>/<see cref="Decremented"/>, an append helper that round-trips events
/// through the serializer (so the daemon's reused <c>InlineProjectionRunner</c> can deserialize
/// them), and factory methods for registrations and a fully-wired daemon.
/// </summary>
public sealed class AsyncProjectionTestKit
{
    /// <summary>Pushdown filter selecting only <see cref="Incremented"/> (<c>typeof(T).Name</c>).</summary>
    public static readonly IReadOnlySet<string> IncrementedOnly = new HashSet<string>(StringComparer.Ordinal) { nameof(Incremented) };

    /// <summary>Pushdown filter selecting only <see cref="Decremented"/>.</summary>
    public static readonly IReadOnlySet<string> DecrementedOnly = new HashSet<string>(StringComparer.Ordinal) { nameof(Decremented) };

    /// <summary>The event-type registry (Incremented / Decremented / UnknownEvent).</summary>
    public EventTypeRegistry Registry { get; } = CounterEventsRegistry.CreateRegistry();

    /// <summary>The serializer bound to <see cref="Registry"/>.</summary>
    public EventSerializer Serializer { get; }

    /// <summary>The in-memory event store the daemon reads the all-stream from.</summary>
    public InMemoryEventStore Store { get; } = new();

    /// <summary>The default in-memory checkpoint sink (a test may substitute its own).</summary>
    public InMemoryCheckpointSink Checkpoints { get; } = new();

    /// <summary>The shared dead-letter buffer the daemon's error policy records poisoned events into (task 5.4).</summary>
    public DeadLetterBuffer DeadLetters { get; } = new();

    /// <summary>Creates a kit with a serializer bound to a fresh registry.</summary>
    public AsyncProjectionTestKit() => Serializer = new EventSerializer(Registry, JsonSerializerOptions.Default);

    /// <summary>Appends <paramref name="events"/> to a single stream, in order, assigning consecutive global positions.</summary>
    public async ValueTask AppendAsync(CancellationToken ct, params object[] events)
    {
        var data = new EventData[events.Length];
        for (var i = 0; i < events.Length; i++)
        {
            data[i] = Serializer.ToEventData(events[i], NewMetadata(), Guid.NewGuid());
        }

        await Store.AppendAsync("stream-1", ExpectedVersion.Any, data, ct);
    }

    /// <summary>A fresh in-memory subscription source over <see cref="Store"/>.</summary>
    public ISubscriptionSource Source() => new InMemorySubscriptionSource(Store);

    /// <summary>Builds a registration wrapping <paramref name="projection"/> in a single-projection runner (default error policy).</summary>
    public AsyncProjectionRegistration Registration(string name, IReadOnlySet<string> eventTypes, object projection)
        => new(name, eventTypes, new InlineProjectionRunner(Serializer, Registry, [projection]));

    /// <summary>Builds a registration wrapping <paramref name="projection"/> in a single-projection runner with an explicit <paramref name="errorPolicy"/> (task 5.4).</summary>
    public AsyncProjectionRegistration Registration(string name, IReadOnlySet<string> eventTypes, object projection, ProjectionErrorPolicy errorPolicy)
        => new(name, eventTypes, new InlineProjectionRunner(Serializer, Registry, [projection]), errorPolicy);

    /// <summary>Builds a fully-wired daemon over the kit's store, with overridable source/sink/clock/logger.</summary>
    public ProjectionDaemon Daemon(
        ProjectionDaemonOptions options,
        IEnumerable<AsyncProjectionRegistration> registrations,
        ISubscriptionSource? source = null,
        ICheckpointSink? checkpoints = null,
        TimeProvider? timeProvider = null,
        ILogger<ProjectionDaemon>? logger = null)
        => new(
            source ?? Source(),
            checkpoints ?? Checkpoints,
            new HwmPoller(Store, options),
            registrations,
            Options.Create(new ActaOptions { Daemon = options }),
            logger ?? NullLogger<ProjectionDaemon>.Instance,
            DeadLetters,
            timeProvider);

    private static EventMetadata NewMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };
}
