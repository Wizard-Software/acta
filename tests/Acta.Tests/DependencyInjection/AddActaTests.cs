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

        Assert.IsType<InMemoryEventStore>(store);
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableEventSerializer()
    {
        using var provider = BuildProvider();

        var serializer = provider.GetRequiredService<EventSerializer>();

        Assert.NotNull(serializer);
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableEventTypeRegistry()
    {
        using var provider = BuildProvider();

        var registry = provider.GetRequiredService<EventTypeRegistry>();

        Assert.NotNull(registry);
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableEventMetadataFactory()
    {
        using var provider = BuildProvider();

        var factory = provider.GetRequiredService<Func<EventMetadata>>();

        Assert.NotNull(factory);
    }

    [Fact]
    public void AddActa_Default_RegistersResolvableAggregateRepositoryForCounterAggregate()
    {
        using var provider = BuildProvider();

        var repository = provider.GetRequiredService<IAggregateRepository<CounterAggregate>>();

        Assert.NotNull(repository);
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameEventStoreSingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<IEventStore>();
        var second = provider.GetRequiredService<IEventStore>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameEventSerializerSingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<EventSerializer>();
        var second = provider.GetRequiredService<EventSerializer>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameEventTypeRegistrySingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<EventTypeRegistry>();
        var second = provider.GetRequiredService<EventTypeRegistry>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddActa_TwoResolves_ReturnSameAggregateRepositorySingletonInstance()
    {
        using var provider = BuildProvider();

        var first = provider.GetRequiredService<IAggregateRepository<CounterAggregate>>();
        var second = provider.GetRequiredService<IAggregateRepository<CounterAggregate>>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddActa_CalledTwice_RegistersExactlyOneEventStoreServiceDescriptor()
    {
        var services = new ServiceCollection();

        services.AddActa();
        services.AddActa();

        Assert.Single(services, d => d.ServiceType == typeof(IEventStore));
    }

    [Fact]
    public void AddActa_ConfigureRegistersEventType_VisibleInResolvedRegistry()
    {
        using var provider = BuildProvider(o => o.Events.Register<Incremented>());

        var registry = provider.GetRequiredService<EventTypeRegistry>();

        Assert.True(registry.TryResolveClrType(nameof(Incremented), out var clrType));
        Assert.Equal(typeof(Incremented), clrType);
    }

    [Fact]
    public void AddActa_DefaultMetadataFactory_YieldsFreshMessageIdWithMatchingCausationAndSetTimestamp()
    {
        using var provider = BuildProvider();
        var factory = provider.GetRequiredService<Func<EventMetadata>>();

        var first = factory();
        var second = factory();

        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.Equal(first.MessageId, first.CorrelationId);
        Assert.Equal(first.MessageId, first.CausationId);
        Assert.NotEqual(default, first.Timestamp);
    }

    [Fact]
    public async Task AddActa_DogfoodEndToEnd_FetchMutateSaveGetByIdRoundTripsThroughResolvedRepository()
    {
        using var provider = BuildProvider(o => o.Events.Register<Incremented>());
        var repository = provider.GetRequiredService<IAggregateRepository<CounterAggregate>>();

        var session = await repository.FetchForWritingAsync("counter-dogfood", Ct);
        Assert.Equal(-1, session.ReadVersion);
        session.Aggregate.AssignId("counter-dogfood");
        session.Aggregate.Increment();
        await repository.SaveAsync(session.Aggregate, session.ReadVersion, Ct);

        var loaded = await repository.GetByIdAsync("counter-dogfood", Ct);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Applied);
    }
}
