using Xunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Acta.Configuration;
using Acta.Tests.TestSupport;

namespace Acta.Tests.DependencyInjection;

/// <summary>
/// Unit tests for the fail-fast <see cref="ActaOptions"/> validation wired by <c>AddActa()</c>
/// (task 3.3, decision D2): <see cref="IStartupValidator"/> resolvability, blocking failures on a
/// null <see cref="ActaOptions.SerializerOptions"/> (both through <see cref="IStartupValidator.Validate"/>
/// and through plain <see cref="IOptions{TOptions}.Value"/> access — proving the validator runs on
/// every options creation, not only at declared "startup"), and the two non-blocking startup
/// <see cref="LogLevel.Warning"/> notices (D14/ADR-014 "SINGLE-PROCESS" and the empty-registry
/// notice) captured through <see cref="ListLoggerProvider"/>.
/// <para>
/// PERF-1 (plan §6/§13): <see cref="IStartupValidator.Validate"/> and <see cref="IOptions{TOptions}.Value"/>
/// resolve <see cref="ActaOptions"/> through two independent caches
/// (<see cref="IOptionsMonitor{TOptions}"/> vs the <see cref="IOptionsFactory{TOptions}"/>-backed
/// <see cref="IOptions{TOptions}"/>), so the validator — and therefore its warnings — may run more
/// than once per process. Every warning assertion below checks "at least once", never "exactly
/// once".
/// </para>
/// </summary>
public sealed class ActaOptionsValidationTests
{
    private static ServiceProvider BuildProvider(ListLoggerProvider logProvider, Action<ActaOptions>? configure = null)
    {
        var services = new ServiceCollection();

        // ActaOptionsValidator is internal to Acta — it cannot be named from this test project.
        // The open-generic ILogger<> -> Logger<> registration lets the container construct
        // ILogger<ActaOptionsValidator> by reflection without this project ever naming that type;
        // SingleProviderLoggerFactory forwards every created logger into logProvider.
        services.AddSingleton<ILoggerFactory>(new SingleProviderLoggerFactory(logProvider));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddActa(configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void GetRequiredService_IStartupValidator_IsResolvable()
    {
        using var provider = BuildProvider(new ListLoggerProvider(), o => o.Events.Register<Incremented>());

        var validator = provider.GetRequiredService<IStartupValidator>();

        validator.Should().NotBeNull();
    }

    [Fact]
    public void Validate_ValidConfiguration_DoesNotThrow()
    {
        using var provider = BuildProvider(new ListLoggerProvider(), o => o.Events.Register<Incremented>());
        var validator = provider.GetRequiredService<IStartupValidator>();

        var exception = Record.Exception(validator.Validate);

        exception.Should().BeNull();
    }

    [Fact]
    public void Validate_SerializerOptionsNull_ThrowsOptionsValidationException()
    {
        using var provider = BuildProvider(new ListLoggerProvider(), o => o.SerializerOptions = null!);
        var validator = provider.GetRequiredService<IStartupValidator>();

        Invoking(validator.Validate).Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void OptionsValue_SerializerOptionsNull_ThrowsOptionsValidationException()
    {
        using var provider = BuildProvider(new ListLoggerProvider(), o => o.SerializerOptions = null!);
        var options = provider.GetRequiredService<IOptions<ActaOptions>>();

        Invoking(() => options.Value).Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Validate_ValidConfiguration_LogsSingleProcessWarningAtLeastOnce()
    {
        var logProvider = new ListLoggerProvider();
        using var provider = BuildProvider(logProvider, o => o.Events.Register<Incremented>());
        var validator = provider.GetRequiredService<IStartupValidator>();

        validator.Validate();

        logProvider.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("SINGLE-PROCESS", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EmptyEventRegistry_LogsEmptyRegistryWarningAtLeastOnce()
    {
        var logProvider = new ListLoggerProvider();
        using var provider = BuildProvider(logProvider);
        var validator = provider.GetRequiredService<IStartupValidator>();

        validator.Validate();

        logProvider.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("no event types are registered", StringComparison.Ordinal));
    }
}
