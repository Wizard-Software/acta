using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.InMemory;
using Acta.Projections.Daemon;
using Acta.Projections.Inline;
using Acta.Serialization;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The DI seam for the asynchronous projection daemon (task 5.2, MODULE-INTERFACES "Rejestracja
/// DI") — registers a projection to be led by <see cref="ProjectionDaemon"/>, and closes decision
/// D8 from task 5.1 by wiring the in-memory <see cref="ISubscriptionSource"/> and
/// <see cref="ICheckpointSink"/> now that a consumer (the daemon) exists.
/// </summary>
public static class AsyncProjectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TProjection"/> — a class implementing one closed
    /// <c>IProjection&lt;TEvent&gt;</c> — as an asynchronous projection led by
    /// <see cref="ProjectionDaemon"/>: the in-memory <see cref="ISubscriptionSource"/> /
    /// <see cref="ICheckpointSink"/> (D8), the <see cref="HwmPoller"/>, the projection itself, its
    /// <see cref="AsyncProjectionRegistration"/> (an <see cref="InlineProjectionRunner"/> over the
    /// single projection, keyed by <paramref name="name"/> and filtered by
    /// <paramref name="eventTypes"/>), and the <see cref="ProjectionDaemon"/> as an
    /// <see cref="IHostedService"/>.
    /// <para>
    /// <b>Prerequisite: call <c>AddActa(...)</c> first.</b> The daemon depends on
    /// <c>EventSerializer</c>, <c>EventTypeRegistry</c>, and <c>IEventStore</c>, all registered by
    /// <c>AddActa</c> — resolving the daemon without a prior <c>AddActa(...)</c> throws at resolution
    /// time. No <c>TimeProvider</c> registration is required: the daemon falls back to
    /// <see cref="TimeProvider.System"/> when the container has none (correction V-1).
    /// </para>
    /// <para>
    /// Registration is idempotent per projection type: calling this method more than once for the
    /// same <typeparamref name="TProjection"/> does not duplicate its registration (nor the shared
    /// infrastructure), while distinct projection types each contribute their own
    /// <see cref="AsyncProjectionRegistration"/> to the daemon.
    /// </para>
    /// </summary>
    /// <typeparam name="TProjection">The concrete projection type to register; must implement a closed <c>IProjection&lt;TEvent&gt;</c>.</typeparam>
    /// <param name="services">The service collection to register the projection into.</param>
    /// <param name="name">The projection name — the checkpoint key. Must be non-empty.</param>
    /// <param name="eventTypes">The event-type filter pushed down to <c>ReadBatchAsync</c>. Must be non-null.</param>
    /// <returns><paramref name="services"/>, to allow fluent chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="eventTypes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    public static IServiceCollection AddActaAsyncProjection<TProjection>(
        this IServiceCollection services, string name, IReadOnlySet<string> eventTypes)
        where TProjection : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(eventTypes);

        // Ports in-memory (closes D8 from 5.1 — the daemon is the consumer that was deferred).
        services.TryAddSingleton<ISubscriptionSource>(sp => new InMemorySubscriptionSource(sp.GetRequiredService<IEventStore>()));
        services.TryAddSingleton<ICheckpointSink>(_ => new InMemoryCheckpointSink());

        // Safe high-water-mark poller (IEventStore + the daemon options carrying VisibilityLag).
        services.TryAddSingleton(sp => new HwmPoller(
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<IOptions<ActaOptions>>().Value.Daemon));

        // The daemon as a hosted service. Type-based (concrete impl type ProjectionDaemon) so
        // TryAddEnumerable dedups against exactly this daemon and coexists with the host's other
        // hosted services; ActivatorUtilities supplies the optional TimeProvider's default (V-1).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ProjectionDaemon>());

        // Projection + its registration — idempotent per projection type; distinct types each add
        // their own AsyncProjectionRegistration to the IEnumerable the daemon consumes. Not a
        // TryAddEnumerable (which would dedup every registration to one shared implementation type).
        if (!ContainsProjection(services, typeof(TProjection)))
        {
            services.AddSingleton<TProjection>();
            services.AddSingleton(sp => new AsyncProjectionRegistration(
                name,
                eventTypes,
                new InlineProjectionRunner(
                    sp.GetRequiredService<EventSerializer>(),
                    sp.GetRequiredService<EventTypeRegistry>(),
                    [sp.GetRequiredService<TProjection>()])));
        }

        return services;
    }

    private static bool ContainsProjection(IServiceCollection services, Type projectionType)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == projectionType)
            {
                return true;
            }
        }

        return false;
    }
}
