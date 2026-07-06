using Xunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Acta.Configuration;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Configuration;

/// <summary>
/// Unit tests for the fail-fast <see cref="ProjectionDaemonOptions"/> validation wired by
/// <c>AddActa()</c> through <c>ActaOptionsValidator</c> (task 5.2): the 03-contracts §2/§3 bounds —
/// <see cref="ProjectionDaemonOptions.BatchSize"/>/<see cref="ProjectionDaemonOptions.PendingEventsThreshold"/>
/// strictly positive, <see cref="ProjectionDaemonOptions.PollingInterval"/>/<see cref="ProjectionDaemonOptions.VisibilityLag"/>
/// strictly positive, <see cref="ProjectionDaemonOptions.GapSafeHarborTimeout"/> non-negative — each
/// asserted through <see cref="IStartupValidator.Validate"/>.
/// </summary>
public sealed class ProjectionDaemonOptionsValidationTests
{
    private static ServiceProvider BuildProvider(Action<ActaOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddActa(configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Validate_DefaultDaemonOptions_DoesNotThrow()
    {
        using var provider = BuildProvider(o => o.Events.Register<Incremented>());
        var validator = provider.GetRequiredService<IStartupValidator>();

        var exception = Record.Exception(validator.Validate);

        exception.Should().BeNull();
    }

    [Fact]
    public void Validate_NullDaemon_ThrowsWithDaemonMessage()
    {
        using var provider = BuildProvider(o => o.Daemon = null!);
        var validator = provider.GetRequiredService<IStartupValidator>();

        Invoking(validator.Validate).Should().Throw<OptionsValidationException>()
            .WithMessage("*Daemon must not be null.*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveBatchSize_ThrowsWithBatchSizeMessage(int batchSize)
    {
        using var provider = BuildProvider(o => o.Daemon.BatchSize = batchSize);
        var validator = provider.GetRequiredService<IStartupValidator>();

        Invoking(validator.Validate).Should().Throw<OptionsValidationException>()
            .WithMessage("*BatchSize must be greater than zero.*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_NonPositivePendingEventsThreshold_ThrowsWithThresholdMessage(int threshold)
    {
        using var provider = BuildProvider(o => o.Daemon.PendingEventsThreshold = threshold);
        var validator = provider.GetRequiredService<IStartupValidator>();

        Invoking(validator.Validate).Should().Throw<OptionsValidationException>()
            .WithMessage("*PendingEventsThreshold must be greater than zero.*");
    }

    [Fact]
    public void Validate_ZeroPollingInterval_ThrowsWithPollingIntervalMessage()
    {
        using var provider = BuildProvider(o => o.Daemon.PollingInterval = TimeSpan.Zero);
        var validator = provider.GetRequiredService<IStartupValidator>();

        Invoking(validator.Validate).Should().Throw<OptionsValidationException>()
            .WithMessage("*PollingInterval must be greater than zero.*");
    }

    [Fact]
    public void Validate_NegativePollingInterval_ThrowsWithPollingIntervalMessage()
    {
        using var provider = BuildProvider(o => o.Daemon.PollingInterval = TimeSpan.FromMilliseconds(-1));
        var validator = provider.GetRequiredService<IStartupValidator>();

        Invoking(validator.Validate).Should().Throw<OptionsValidationException>()
            .WithMessage("*PollingInterval must be greater than zero.*");
    }

    [Fact]
    public void Validate_ZeroVisibilityLag_ThrowsWithVisibilityLagMessage()
    {
        using var provider = BuildProvider(o => o.Daemon.VisibilityLag = TimeSpan.Zero);
        var validator = provider.GetRequiredService<IStartupValidator>();

        Invoking(validator.Validate).Should().Throw<OptionsValidationException>()
            .WithMessage("*VisibilityLag must be greater than zero.*");
    }

    [Fact]
    public void Validate_NegativeGapSafeHarborTimeout_ThrowsWithGapMessage()
    {
        using var provider = BuildProvider(o => o.Daemon.GapSafeHarborTimeout = TimeSpan.FromSeconds(-1));
        var validator = provider.GetRequiredService<IStartupValidator>();

        Invoking(validator.Validate).Should().Throw<OptionsValidationException>()
            .WithMessage("*GapSafeHarborTimeout must not be negative.*");
    }

    [Fact]
    public void Validate_ZeroGapSafeHarborTimeout_DoesNotThrow()
    {
        using var provider = BuildProvider(o =>
        {
            o.Events.Register<Incremented>();
            o.Daemon.GapSafeHarborTimeout = TimeSpan.Zero;
        });
        var validator = provider.GetRequiredService<IStartupValidator>();

        var exception = Record.Exception(validator.Validate);

        exception.Should().BeNull();
    }
}
