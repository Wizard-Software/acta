using Xunit;

using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.Correlation;
using Acta.Diagnostics;
using Acta.InMemory;
using Acta.Serialization;
using Acta.Tests.TestSupport;

namespace Acta.Tests.DependencyInjection;

/// <summary>
/// Unit tests for <c>AddActa()</c> (task 3.3, composition root, MODULE-INTERFACES "Rejestracja
/// DI"): resolvability of every Tier 1 component, singleton identity, idempotent
/// re-registration, the <c>configure</c> delegate being applied, the shape of the default
/// <see cref="EventMetadata"/> factory, and a dogfood end-to-end round trip through a resolved
/// <see cref="IAggregateRepository{CounterAggregate}"/>.
/// </summary>
public sealed class AddActaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider BuildProvider(Action<ActaOptions>? configure = null)
    {
        var services = new ServiceCollection();

        // ActaOptionsValidator (wired by AddActa) depends on ILogger<T> — a real host always has
        // logging configured; here a no-op ILoggerFactory + the open-generic Logger<T> wrapper
        // (both from Microsoft.Extensions.Logging.Abstractions) satisfy that dependency without
        // asserting anything about what gets logged.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddActa(configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableInMemoryEventStore()
    {
        using var provider = BuildProvider();

        var store = provider.GetRequiredService<IEventStore>();

        store.Should().BeOfType<InMemoryEventStore>();
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableEventSerializer()
    {
        using var provider = BuildProvider();

        var serializer = provider.GetRequiredService<EventSerializer>();

        serializer.Should().NotBeNull();
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableEventTypeRegistry()
    {
        using var provider = BuildProvider();

        var registry = provider.GetRequiredService<EventTypeRegistry>();

        registry.Should().NotBeNull();
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableEventMetadataFactory()
    {
        using var provider = BuildProvider();

        var factory = provider.GetRequiredService<Func<EventMetadata>>();

        factory.Should().NotBeNull();
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableAggregateRepositoryForCounterAggregate()
    {
        using var provider = BuildProvider();

        var repository = provider.GetRequiredService<IAggregateRepository<CounterAggregate>>();

        repository.Should().NotBeNull();
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameEventStoreSingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<IEventStore>();
        var second = provider.GetRequiredService<IEventStore>();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameEventSerializerSingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<EventSerializer>();
        var second = provider.GetRequiredService<EventSerializer>();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameEventTypeRegistrySingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<EventTypeRegistry>();
        var second = provider.GetRequiredService<EventTypeRegistry>();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameAggregateRepositorySingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<IAggregateRepository<CounterAggregate>>();
        var second = provider.GetRequiredService<IAggregateRepository<CounterAggregate>>();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_CalledTwice_RegistersExactlyOneEventStoreServiceDescriptor()
    {
        var services = new ServiceCollection();

        services.AddActa();
        services.AddActa();

        services.Should().ContainSingle(d => d.ServiceType == typeof(IEventStore));
    }

    [Fact]
    public void AddActa_ConfigureRegistersEventType_VisibleInResolvedRegistry()
    {
        using var provider = BuildProvider(o => o.Events.Register<Incremented>());

        var registry = provider.GetRequiredService<EventTypeRegistry>();

        registry.TryResolveClrType(nameof(Incremented), out var clrType).Should().BeTrue();
        clrType.Should().Be(typeof(Incremented));
    }

    [Fact]
    public void AddActa_DefaultMetadataFactory_YieldsFreshMessageIdWithMatchingCausationAndSetTimestamp()
    {
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<Func<EventMetadata>>();

        var first = factory();
        var second = factory();

        second.MessageId.Should().NotBe(first.MessageId);
        first.CorrelationId.Should().Be(first.MessageId);
        first.CausationId.Should().Be(first.MessageId);
        first.Timestamp.Should().NotBe(default);
    }

    [Fact]
    public async Task AddActa_DogfoodEndToEnd_FetchMutateSaveGetByIdRoundTripsThroughResolvedRepository()
    {
        using var provider = BuildProvider(o => o.Events.Register<Incremented>());
        var repository = provider.GetRequiredService<IAggregateRepository<CounterAggregate>>();

        var session = await repository.FetchForWritingAsync("counter-dogfood", Ct);
        session.ReadVersion.Should().Be(-1);
        session.Aggregate.AssignId("counter-dogfood");
        session.Aggregate.Increment();
        await repository.SaveAsync(session.Aggregate, session.ReadVersion, Ct);

        var loaded = await repository.GetByIdAsync("counter-dogfood", Ct);

        loaded.Should().NotBeNull();
        loaded.Applied.Should().Be(1);
    }

    [Fact]
    public void AddActa_NullServices_ThrowsArgumentNullException()
    {
        IServiceCollection? services = null;

        Invoking(() => services!.AddActa()).Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("services");
    }

    /// <summary>
    /// Guards against a false-negative on the test above: <c>Microsoft.Extensions.Options</c>'s own
    /// <c>AddOptions&lt;TOptions&gt;()</c> ALSO null-checks its <c>services</c> parameter and throws
    /// an <see cref="ArgumentNullException"/> with the identical <see cref="ArgumentException.ParamName"/>
    /// ("services") — so removing <c>AddActa</c>'s own <c>ArgumentNullException.ThrowIfNull(services)</c>
    /// guard would still produce an outwardly identical exception type/ParamName, just thrown one frame
    /// deeper inside the framework's own <c>OptionsServiceCollectionExtensions.AddOptions</c>. Asserting
    /// the stack trace does NOT mention that framework method proves <c>AddActa</c>'s OWN guard is what
    /// fired, before ever reaching <c>services.AddOptions&lt;ActaOptions&gt;()</c>.
    /// </summary>
    [Fact]
    public void AddActa_NullServices_ThrowsFromItsOwnGuard_BeforeReachingAddOptions()
    {
        IServiceCollection? services = null;

        var ex = Invoking(() => services!.AddActa()).Should().Throw<ArgumentNullException>().Which;

        ex.StackTrace.Should().NotContain("OptionsServiceCollectionExtensions");
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableEventStoreMetrics()
    {
        using var provider = BuildProvider();

        var metrics = provider.GetRequiredService<EventStoreMetrics>();

        metrics.Should().NotBeNull();
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameEventStoreMetricsSingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<EventStoreMetrics>();
        var second = provider.GetRequiredService<EventStoreMetrics>();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_TimeProviderRegisteredInContainer_MetadataFactoryUsesRegisteredTimeProviderNotSystemClock()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        var fixedInstant = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(fixedInstant));

        services.AddActa();
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<Func<EventMetadata>>();
        var metadata = factory();

        metadata.Timestamp.Should().Be(fixedInstant);
    }

    [Fact]
    public void AddActa_RegistersResolvableCorrelationContextAccessor()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ICorrelationContextAccessor>();
        first.Should().BeOfType<AsyncLocalCorrelationContextAccessor>();

        var second = provider.GetRequiredService<ICorrelationContextAccessor>();
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableInMemoryLeaderElector()
    {
        using var provider = BuildProvider();

        var elector = provider.GetRequiredService<ILeaderElector>();

        elector.Should().BeOfType<InMemoryLeaderElector>();
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameLeaderElectorSingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ILeaderElector>();
        var second = provider.GetRequiredService<ILeaderElector>();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_CalledTwice_RegistersExactlyOneLeaderElectorServiceDescriptor()
    {
        var services = new ServiceCollection();

        services.AddActa();
        services.AddActa();

        services.Should().ContainSingle(d => d.ServiceType == typeof(ILeaderElector));
    }

    [Fact]
    public void MetadataFactory_WithinCorrelationScope_InheritsCorrelationAndCausation()
    {
        using var provider = BuildProvider();
        var accessor = provider.GetRequiredService<ICorrelationContextAccessor>();
        var factory = provider.GetRequiredService<Func<EventMetadata>>();

        var scopeContext = new CorrelationContext
        {
            CorrelationId = Guid.CreateVersion7(),
            CausationId = Guid.CreateVersion7(),
            User = new UserRef("technical-account-id"),
            TenantId = "tenant-1",
            TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            TraceState = "vendor=value",
        };

        EventMetadata metadata;
        using (accessor.BeginScope(scopeContext))
        {
            metadata = factory();
        }

        metadata.CorrelationId.Should().Be(scopeContext.CorrelationId);
        metadata.CausationId.Should().Be(scopeContext.CausationId);
        metadata.MessageId.Should().NotBe(scopeContext.CorrelationId);
        metadata.MessageId.Should().NotBe(scopeContext.CausationId);
        metadata.User.Should().Be(scopeContext.User);
        metadata.TenantId.Should().Be(scopeContext.TenantId);
        metadata.TraceParent.Should().Be(scopeContext.TraceParent);
        metadata.TraceState.Should().Be(scopeContext.TraceState);
    }

    [Fact]
    public void MetadataFactory_NoScope_RemainsRootSelfCorrelated()
    {
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<Func<EventMetadata>>();

        var metadata = factory();

        metadata.CorrelationId.Should().Be(metadata.MessageId);
        metadata.CausationId.Should().Be(metadata.MessageId);
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableInMemorySnapshotStore()
    {
        using var provider = BuildProvider();

        var store = provider.GetRequiredService<ISnapshotStore>();

        store.Should().BeOfType<InMemorySnapshotStore>();
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameSnapshotStoreSingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<ISnapshotStore>();
        var second = provider.GetRequiredService<ISnapshotStore>();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_CalledTwice_RegistersExactlyOneSnapshotStoreServiceDescriptor()
    {
        var services = new ServiceCollection();

        services.AddActa();
        services.AddActa();

        services.Should().ContainSingle(d => d.ServiceType == typeof(ISnapshotStore));
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableAggregateRepositoryForSnapshotCounter()
    {
        using var provider = BuildProvider();

        var repository = provider.GetRequiredService<IAggregateRepository<SnapshotCounter>>();

        repository.Should().NotBeNull();
    }

    /// <summary>
    /// Task 6.1 (n): proves the snapshot-first path is genuinely wired end-to-end through
    /// <c>AddActa()</c> — not merely that both services happen to resolve. The planted snapshot
    /// deliberately claims a <see cref="SnapshotCounter.Value"/> the real event stream never
    /// produced, so the assertion below can only pass if the resolved repository actually restored
    /// FROM this snapshot instead of replaying events.
    /// </summary>
    [Fact]
    public async Task AddActa_ResolvedSnapshotCounterRepository_ConsultsTheInjectedSnapshotStoreOnLoad()
    {
        using var provider = BuildProvider(o => o.Events.Register<Incremented>().Register<Decremented>());
        var repository = provider.GetRequiredService<IAggregateRepository<SnapshotCounter>>();
        var snapshotStore = provider.GetRequiredService<ISnapshotStore>();

        var writer = new SnapshotCounter();
        writer.AssignId("counter-di-snapshot");
        writer.Increment();
        await repository.SaveAsync(writer, ExpectedVersion.NoStream, Ct); // stream holds exactly one event (Value == 1)

        var plantedState = JsonSerializer.SerializeToUtf8Bytes(new PlantedState(99));
        await snapshotStore.SaveAsync(
            new Snapshot("counter-di-snapshot", writer.Version, writer.SnapshotSchemaVersion, plantedState, DateTimeOffset.UtcNow), Ct);

        var loaded = await repository.GetByIdAsync("counter-di-snapshot", Ct);

        loaded.Should().NotBeNull();
        loaded!.Value.Should().Be(99);
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableInMemoryReservationStore()
    {
        using var provider = BuildProvider();

        var store = provider.GetRequiredService<IReservationStore>();

        store.Should().BeOfType<InMemoryReservationStore>();
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameReservationStoreSingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<IReservationStore>();
        var second = provider.GetRequiredService<IReservationStore>();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_CalledTwice_RegistersExactlyOneReservationStoreServiceDescriptor()
    {
        var services = new ServiceCollection();

        services.AddActa();
        services.AddActa();

        services.Should().ContainSingle(d => d.ServiceType == typeof(IReservationStore));
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableInMemoryIdempotencyStore()
    {
        using var provider = BuildProvider();

        var store = provider.GetRequiredService<IIdempotencyStore>();

        store.Should().BeOfType<InMemoryIdempotencyStore>();
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameIdempotencyStoreSingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<IIdempotencyStore>();
        var second = provider.GetRequiredService<IIdempotencyStore>();

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddActa_CalledTwice_RegistersExactlyOneIdempotencyStoreServiceDescriptor()
    {
        var services = new ServiceCollection();

        services.AddActa();
        services.AddActa();

        services.Should().ContainSingle(d => d.ServiceType == typeof(IIdempotencyStore));
    }

    private readonly record struct PlantedState(int Value);

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
