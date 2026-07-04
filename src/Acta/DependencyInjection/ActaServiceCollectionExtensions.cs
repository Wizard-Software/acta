using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Acta.Abstractions;
using Acta.Aggregates;
using Acta.Configuration;
using Acta.InMemory;
using Acta.Serialization;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The Tier 1 composition root for Acta (MODULE-INTERFACES "Rejestracja DI") — registers the
/// in-memory event store, the event serialization pipeline, the aggregate repository, and a
/// default <see cref="EventMetadata"/> factory, plus a fail-fast configuration validator run at
/// host startup.
/// </summary>
public static class ActaServiceCollectionExtensions
{
    /// <summary>
    /// Registers Acta's Tier 1 components into <paramref name="services"/>: an in-memory
    /// <see cref="IEventStore"/>, an <see cref="EventTypeRegistry"/> and <see cref="EventSerializer"/>
    /// built from <see cref="ActaOptions"/>, a default <see cref="EventMetadata"/> factory, and the
    /// open-generic <see cref="IAggregateRepository{TAggregate}"/> implementation.
    /// <para>
    /// Registration is idempotent (<see cref="ServiceCollectionDescriptorExtensions.TryAdd"/> /
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable"/> throughout, convention
    /// 1.4): calling this method more than once on the same <see cref="IServiceCollection"/> does
    /// not duplicate any service descriptor. All registered Tier 1 components are singletons — see
    /// each component's own remarks for why a shared singleton instance is safe.
    /// </para>
    /// <para>
    /// Configuration is validated fail-fast at host startup via
    /// <see cref="OptionsBuilderExtensions.ValidateOnStart{TOptions}"/> (no dependency on
    /// <c>Microsoft.Extensions.Hosting</c> is introduced by this call): an invalid
    /// <see cref="ActaOptions"/> (a null <see cref="ActaOptions.SerializerOptions"/> or
    /// <see cref="ActaOptions.Events"/>) throws <see cref="OptionsValidationException"/> before the
    /// host starts serving traffic, and a startup <c>Warning</c> log entry documents that the
    /// registered backend is the in-memory store — SINGLE-PROCESS ONLY (ADR-014, D14).
    /// </para>
    /// </summary>
    /// <param name="services">The service collection to register Acta's components into.</param>
    /// <param name="configure">
    /// An optional delegate to configure <see cref="ActaOptions"/> — typically used to register
    /// event types (<c>o.Events.Register&lt;T&gt;()</c>) and to override
    /// <see cref="ActaOptions.SerializerOptions"/>.
    /// </param>
    /// <returns><paramref name="services"/>, to allow fluent chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddActa(this IServiceCollection services, Action<ActaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<ActaOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder.ValidateOnStart();

        // Fail-fast validator (enumerable — TryAddEnumerable allows multiple IValidateOptions<T>
        // registrations to coexist without one call to AddActa evicting another's).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ActaOptions>, ActaOptionsValidator>());

        // Registry and serializer, both sourced from the resolved ActaOptions.
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<ActaOptions>>().Value.Events);
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ActaOptions>>().Value;
            return new EventSerializer(options.Events, options.SerializerOptions);
        });

        // In-memory backend (single-process ONLY — D14). TimeProvider comes from the container
        // when the host registered one; InMemoryEventStore falls back to TimeProvider.System.
        // SEC-1 (plan Q2): ActaOptions.MaxEventPayloadSize/MaxAppendBatchSize do not exist yet —
        // enforcing them belongs to the append path (InMemoryEventStore.AppendAsync), not to this
        // composition root. Deferred to a Feature 7 task; acceptable for Tier 1 in-memory,
        // single-process (ADR-014) where the exposure is bounded by process memory.
        services.TryAddSingleton<IEventStore>(sp => new InMemoryEventStore(sp.GetService<TimeProvider>()));

        // Default EventMetadata factory (MODULE-INTERFACES Grupa 3 seam; superseded by a
        // correlation-accessor-based factory in Grupa 6). Invoked once per SaveAsync call, so a
        // singleton registration still yields a fresh MessageId per command.
        services.TryAddSingleton<Func<EventMetadata>>(sp =>
        {
            var clock = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return () =>
            {
                var id = Guid.NewGuid();
                return new EventMetadata
                {
                    MessageId = id,
                    CorrelationId = id,
                    CausationId = id,
                    Timestamp = clock.GetUtcNow(),
                };
            };
        });

        // Aggregate repository — open generic; the container supplies the optional
        // eventIdFactory constructor parameter's default value (null -> AggregateRepository's own
        // deterministic FNV-1a derivation).
        services.TryAddSingleton(typeof(IAggregateRepository<>), typeof(AggregateRepository<>));

        return services;
    }
}
