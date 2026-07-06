using Xunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.Projections.Inline;
using Acta.Serialization;

namespace Acta.Tests.DependencyInjection;

/// <summary>
/// Unit tests for <c>AddInlineProjection&lt;TProjection&gt;()</c> (task 4.1, MODULE-INTERFACES
/// "Rejestracja DI"): idempotent registration of both the projection descriptor and the shared
/// <see cref="InlineProjectionRunner"/>, and end-to-end resolvability — with the registered
/// projection actually injected and dispatched to — once <c>AddActa(...)</c> has run.
/// </summary>
public sealed class AddInlineProjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record SampleProjectionEvent(string Value);

    private sealed class SampleProjection : IProjection<SampleProjectionEvent>
    {
        public List<SampleProjectionEvent> Applied { get; } = [];

        public ValueTask ApplyAsync(SampleProjectionEvent @event, StoredEvent raw, CancellationToken ct = default)
        {
            Applied.Add(@event);
            return ValueTask.CompletedTask;
        }
    }

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    private static ServiceProvider BuildProvider(Action<ActaOptions>? configure = null)
    {
        var services = new ServiceCollection();

        // Mirrors AddActaTests.BuildProvider (task 3.3): ActaOptionsValidator depends on ILogger<T>.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddActa(configure);
        services.AddInlineProjection<SampleProjection>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddInlineProjection_CalledTwice_RegistersExactlyOneProjectionDescriptor()
    {
        var services = new ServiceCollection();

        services.AddInlineProjection<SampleProjection>();
        services.AddInlineProjection<SampleProjection>();

        services.Should().ContainSingle(d => d.ServiceType == typeof(object) && d.ImplementationType == typeof(SampleProjection));
    }

    [Fact]
    public void AddInlineProjection_CalledTwice_RegistersExactlyOneRunnerDescriptor()
    {
        var services = new ServiceCollection();

        services.AddInlineProjection<SampleProjection>();
        services.AddInlineProjection<SampleProjection>();

        services.Should().ContainSingle(d => d.ServiceType == typeof(InlineProjectionRunner));
    }

    [Fact]
    public void AddInlineProjection_AfterAddActa_RunnerIsResolvable()
    {
        using var provider = BuildProvider();

        var runner = provider.GetRequiredService<InlineProjectionRunner>();

        runner.Should().NotBeNull();
    }

    [Fact]
    public async Task AddInlineProjection_ResolvedRunner_DispatchesToTheInjectedProjection()
    {
        using var provider = BuildProvider(o => o.Events.Register<SampleProjectionEvent>());
        var runner = provider.GetRequiredService<InlineProjectionRunner>();
        var serializer = provider.GetRequiredService<EventSerializer>();
        var sample = provider.GetServices<object>().OfType<SampleProjection>().Should().ContainSingle().Subject;

        var eventData = serializer.ToEventData(new SampleProjectionEvent("v"), CreateMetadata(), Guid.NewGuid());
        var stored = new StoredEvent(
            eventData.EventId,
            "stream-1",
            0,
            new GlobalPosition(1),
            eventData.EventType,
            eventData.SchemaVersion,
            eventData.Payload,
            eventData.Metadata,
            DateTimeOffset.UtcNow);

        await runner.RunAsync([stored], Ct);

        sample.Applied.Should().ContainSingle().Which.Value.Should().Be("v");
    }
}
