using Xunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Acta.Abstractions;
using Acta.Configuration;
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

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
