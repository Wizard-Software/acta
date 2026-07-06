using Xunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.Projections.Daemon;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Unit tests for <c>AddActaAsyncProjection&lt;TProjection&gt;()</c> (task 5.2, step 11): the ports
/// (D8), <see cref="HwmPoller"/>, per-projection <see cref="AsyncProjectionRegistration"/>, and the
/// <see cref="ProjectionDaemon"/> hosted service are registered; the daemon resolves WITHOUT a
/// registered <see cref="TimeProvider"/> (correction V-1); registration is idempotent per projection
/// type while distinct projections each add their own registration; and resolving without a prior
/// <c>AddActa()</c> throws.
/// </summary>
public sealed class AsyncProjectionServiceCollectionExtensionsTests
{
    private static readonly IReadOnlySet<string> SampleTypes = new HashSet<string>(StringComparer.Ordinal) { nameof(SampleEvent) };
    private static readonly IReadOnlySet<string> OtherTypes = new HashSet<string>(StringComparer.Ordinal) { nameof(OtherEvent) };

    private sealed record SampleEvent(string Value);

    private sealed record OtherEvent(string Value);

    private sealed class SampleProjection : IProjection<SampleEvent>
    {
        public ValueTask ApplyAsync(SampleEvent @event, StoredEvent raw, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class OtherProjection : IProjection<OtherEvent>
    {
        public ValueTask ApplyAsync(OtherEvent @event, StoredEvent raw, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        // ActaOptionsValidator (and ProjectionDaemon) depend on ILogger<T>; no concrete logging package.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return services;
    }

    [Fact]
    public void AddActaAsyncProjection_RegistersPortsPollerRegistrationAndHostedService()
    {
        var services = BaseServices();

        services.AddActa();
        services.AddActaAsyncProjection<SampleProjection>("sample", SampleTypes);

        services.Should().Contain(d => d.ServiceType == typeof(ISubscriptionSource));
        services.Should().Contain(d => d.ServiceType == typeof(ICheckpointSink));
        services.Should().Contain(d => d.ServiceType == typeof(HwmPoller));
        services.Should().Contain(d => d.ServiceType == typeof(AsyncProjectionRegistration));
        services.Should().Contain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ProjectionDaemon));
    }

    [Fact]
    public void AddActaAsyncProjection_WithoutRegisteredTimeProvider_ResolvesTheDaemon()
    {
        var services = BaseServices();
        services.AddActa();
        services.AddActaAsyncProjection<SampleProjection>("sample", SampleTypes);
        using var provider = services.BuildServiceProvider();

        // V-1: no TimeProvider is registered; the daemon must still resolve (falls back to System).
        var hosted = provider.GetServices<IHostedService>().OfType<ProjectionDaemon>();

        hosted.Should().ContainSingle();
    }

    [Fact]
    public void AddActaAsyncProjection_CalledTwiceForSameProjection_RegistersOneRegistrationAndOneDaemon()
    {
        var services = BaseServices();
        services.AddActa();

        services.AddActaAsyncProjection<SampleProjection>("sample", SampleTypes);
        services.AddActaAsyncProjection<SampleProjection>("sample", SampleTypes);

        services.Should().ContainSingle(d => d.ServiceType == typeof(AsyncProjectionRegistration));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ProjectionDaemon));
    }

    [Fact]
    public void AddActaAsyncProjection_TwoDistinctProjections_ResolvesBothRegistrations()
    {
        var services = BaseServices();
        services.AddActa();
        services.AddActaAsyncProjection<SampleProjection>("sample", SampleTypes);
        services.AddActaAsyncProjection<OtherProjection>("other", OtherTypes);
        using var provider = services.BuildServiceProvider();

        var registrations = provider.GetServices<AsyncProjectionRegistration>();

        registrations.Select(r => r.Name).Should().BeEquivalentTo(["sample", "other"]);
    }

    [Fact]
    public void AddActaAsyncProjection_ResolvedRegistration_CarriesNameAndEventTypes()
    {
        var services = BaseServices();
        services.AddActa();
        services.AddActaAsyncProjection<SampleProjection>("sample", SampleTypes);
        using var provider = services.BuildServiceProvider();

        var registration = provider.GetServices<AsyncProjectionRegistration>().Should().ContainSingle().Subject;

        registration.Name.Should().Be("sample");
        registration.EventTypes.Should().BeEquivalentTo(SampleTypes);
    }

    [Fact]
    public void AddActaAsyncProjection_WithoutAddActa_ResolvingDaemonThrows()
    {
        var services = BaseServices();
        services.AddActaAsyncProjection<SampleProjection>("sample", SampleTypes);
        using var provider = services.BuildServiceProvider();

        // No AddActa → no IEventStore/EventSerializer/IOptions<ActaOptions>; the daemon cannot be built.
        Invoking(() => provider.GetServices<IHostedService>().ToList())
            .Should().Throw<InvalidOperationException>();
    }
}
