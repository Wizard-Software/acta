using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Acta.Abstractions;
using Acta.Aggregates;
using Acta.Configuration;
using Acta.Correlation;
using Acta.Diagnostics;
using Acta.InMemory;
using Acta.Serialization;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The Tier 1 composition root for Acta (MODULE-INTERFACES "Rejestracja DI") — registers the
/// in-memory event store, the in-memory snapshot store, the event serialization pipeline, the
/// aggregate repository, and a default <see cref="EventMetadata"/> factory, plus a fail-fast
/// configuration validator run at host startup.
/// </summary>
public static class ActaServiceCollectionExtensions
{
    /// <summary>
    /// Registers Acta's Tier 1 components into <paramref name="services"/>: an in-memory
    /// <see cref="IEventStore"/>, an in-memory <see cref="ISnapshotStore"/> (task 6.1, FR-4/ADR-006),
    /// an <see cref="EventTypeRegistry"/> and <see cref="EventSerializer"/> built from
    /// <see cref="ActaOptions"/>, a default <see cref="EventMetadata"/> factory, and the
    /// open-generic <see cref="IAggregateRepository{TAggregate}"/> implementation — which MS.DI
    /// wires to the registered <see cref="ISnapshotStore"/> through that type's optional
    /// constructor parameter, enabling the snapshot-first read path for any
    /// <c>TAggregate</c> that also implements <see cref="ISnapshotableAggregate"/>.
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

        // Diagnostics (task 8.6, D3 pattern — falls back to new Meter("Acta") when the host never
        // called AddMetrics()). Registered here — not in AsyncProjectionServiceCollectionExtensions
        // (GAP-2) — because InMemoryEventStore itself is registered by THIS method, and any host that
        // never calls AddActaAsyncProjection (or calls AddActaPostgres instead) must still get
        // acta.append.throughput instrumentation. SubscriptionMetrics is reserved (D-5, wired in R3)
        // but registered alongside so it is resolvable the same way.
        services.TryAddSingleton<EventStoreMetrics>();
        services.TryAddSingleton<SubscriptionMetrics>();

        // In-memory backend (single-process ONLY — D14). TimeProvider comes from the container
        // when the host registered one; InMemoryEventStore falls back to TimeProvider.System.
        // SEC-1 (plan Q2): ActaOptions.MaxEventPayloadSize/MaxAppendBatchSize do not exist yet —
        // enforcing them belongs to the append path (InMemoryEventStore.AppendAsync), not to this
        // composition root. Deferred to a Feature 7 task; acceptable for Tier 1 in-memory,
        // single-process (ADR-014) where the exposure is bounded by process memory.
        services.TryAddSingleton<IEventStore>(sp => new InMemoryEventStore(
            sp.GetService<TimeProvider>(),
            sp.GetService<EventStoreMetrics>(),
            sp.GetService<ILogger<InMemoryEventStore>>()));

        // Reservation store (task 8.5, FR-16/ADR-009) and idempotency store (task 8.5, FR-7/ADR-003)
        // — in-memory, best-effort, single-process ONLY (D14), same multi-pod caveat as the event
        // store above. AddActaPostgres replaces both with the durable, database-enforced backends.
        services.TryAddSingleton<IReservationStore>(sp => new InMemoryReservationStore(sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IIdempotencyStore>(sp => new InMemoryIdempotencyStore(sp.GetService<TimeProvider>()));

        // Snapshot store (task 6.1, FR-4/ADR-006) — in-memory, single-process ONLY (D14), same
        // multi-pod caveat as the event store above. Registered BEFORE the aggregate repository
        // below so the container can supply it to that open generic's optional constructor
        // parameter (MS.DI resolves constructor parameters lazily on first request, so
        // registration order here is documentation, not a hard requirement — kept anyway for
        // readability: readers see the dependency before its consumer).
        services.TryAddSingleton<ISnapshotStore>(_ => new InMemorySnapshotStore());

        // Leader elector (task 7.5, ADR-005) — in-memory default: single-process, single-active per
        // (projection, tenant), no cross-pod election (ADR-014, D14). AddActaPostgres replaces this
        // with the advisory-lock elector. TryAdd — additive and idempotent.
        services.TryAddSingleton<ILeaderElector, InMemoryLeaderElector>();

        // Correlation accessor (Grupa 6, FR-9) — singleton, idempotent registration.
        services.TryAddSingleton<ICorrelationContextAccessor, AsyncLocalCorrelationContextAccessor>();

        // EventMetadata factory — the 3-ID rule read from the correlation accessor (Grupa 6, FR-9;
        // supersedes the original context-free factory, R-2). Within a correlation scope the stamped
        // event inherits the scope's CorrelationId/CausationId (and User/TenantId/trace parent+state);
        // with no scope active the factory stays backwards-compatible with the original root behaviour
        // — a fresh MessageId with CorrelationId == CausationId == MessageId (each command is the root
        // of its own conversation). The core never reads Activity.Current — trace fields flow only from
        // a seeded context (ADR-011 forbidden boundaries, DP-7).
        services.TryAddSingleton<Func<EventMetadata>>(sp =>
        {
            var clock = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var accessor = sp.GetRequiredService<ICorrelationContextAccessor>();
            return () =>
            {
                var messageId = Guid.NewGuid();
                var ctx = accessor.Current;
                return new EventMetadata
                {
                    MessageId = messageId,
                    CorrelationId = ctx?.CorrelationId ?? messageId,   // root: no context -> self
                    CausationId = ctx?.CausationId ?? messageId,       // root: no context -> self
                    Timestamp = clock.GetUtcNow(),
                    User = ctx?.User,
                    TenantId = ctx?.TenantId,
                    TraceParent = ctx?.TraceParent,
                    TraceState = ctx?.TraceState,
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
