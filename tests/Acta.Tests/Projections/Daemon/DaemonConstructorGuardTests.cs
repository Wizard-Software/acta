using Xunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.InMemory;
using Acta.Projections.Daemon;
using Acta.Projections.Inline;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Guards the constructor / argument-validation contracts of the async-projection components (task 5.2
/// -5.4): each required collaborator is null-checked, each name is non-empty, and the DI extension
/// validates its arguments — so a removed guard is observable rather than surfacing as a later
/// <see cref="NullReferenceException"/>. Also pins <c>SetGapObserved</c>'s "first observation wins"
/// idempotency (task 5.3).
/// </summary>
public sealed class DaemonConstructorGuardTests
{
    private static readonly AsyncProjectionTestKit Kit = new();
    private static readonly ProjectionDaemonOptions Options = new();

    private static InlineProjectionRunner NewRunner()
        => new(Kit.Serializer, Kit.Registry, [new RecordingProjection<Incremented>()]);

    private static GapGuard NewGapGuard()
        => new(Options, new ProjectionDaemonMetrics(), NullLogger<GapGuard>.Instance);

    // ── ProjectionDaemon: all eight collaborators are required ────────────────────────────────────

    [Fact]
    public void ProjectionDaemon_NullSource_Throws()
    {
        var store = new InMemoryEventStore();
        Invoking(() => new ProjectionDaemon(
            null!, new InMemoryCheckpointSink(), new HwmPoller(store, Options),
            [], Microsoft.Extensions.Options.Options.Create(new ActaOptions { Daemon = Options }),
            NullLogger<ProjectionDaemon>.Instance, new DeadLetterBuffer(), NewGapGuard()))
            .Should().Throw<ArgumentNullException>().WithParameterName("source");
    }

    [Fact]
    public void ProjectionDaemon_NullCheckpoints_Throws()
    {
        var store = new InMemoryEventStore();
        Invoking(() => new ProjectionDaemon(
            new InMemorySubscriptionSource(store), null!, new HwmPoller(store, Options),
            [], Microsoft.Extensions.Options.Options.Create(new ActaOptions { Daemon = Options }),
            NullLogger<ProjectionDaemon>.Instance, new DeadLetterBuffer(), NewGapGuard()))
            .Should().Throw<ArgumentNullException>().WithParameterName("checkpoints");
    }

    [Fact]
    public void ProjectionDaemon_NullHwmPoller_Throws()
    {
        var store = new InMemoryEventStore();
        Invoking(() => new ProjectionDaemon(
            new InMemorySubscriptionSource(store), new InMemoryCheckpointSink(), null!,
            [], Microsoft.Extensions.Options.Options.Create(new ActaOptions { Daemon = Options }),
            NullLogger<ProjectionDaemon>.Instance, new DeadLetterBuffer(), NewGapGuard()))
            .Should().Throw<ArgumentNullException>().WithParameterName("hwmPoller");
    }

    [Fact]
    public void ProjectionDaemon_NullProjections_Throws()
    {
        var store = new InMemoryEventStore();
        Invoking(() => new ProjectionDaemon(
            new InMemorySubscriptionSource(store), new InMemoryCheckpointSink(), new HwmPoller(store, Options),
            null!, Microsoft.Extensions.Options.Options.Create(new ActaOptions { Daemon = Options }),
            NullLogger<ProjectionDaemon>.Instance, new DeadLetterBuffer(), NewGapGuard()))
            .Should().Throw<ArgumentNullException>().WithParameterName("projections");
    }

    [Fact]
    public void ProjectionDaemon_NullOptions_Throws()
    {
        var store = new InMemoryEventStore();
        Invoking(() => new ProjectionDaemon(
            new InMemorySubscriptionSource(store), new InMemoryCheckpointSink(), new HwmPoller(store, Options),
            [], null!, NullLogger<ProjectionDaemon>.Instance, new DeadLetterBuffer(), NewGapGuard()))
            .Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    [Fact]
    public void ProjectionDaemon_NullLogger_Throws()
    {
        var store = new InMemoryEventStore();
        Invoking(() => new ProjectionDaemon(
            new InMemorySubscriptionSource(store), new InMemoryCheckpointSink(), new HwmPoller(store, Options),
            [], Microsoft.Extensions.Options.Options.Create(new ActaOptions { Daemon = Options }),
            null!, new DeadLetterBuffer(), NewGapGuard()))
            .Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void ProjectionDaemon_NullDeadLetters_Throws()
    {
        var store = new InMemoryEventStore();
        Invoking(() => new ProjectionDaemon(
            new InMemorySubscriptionSource(store), new InMemoryCheckpointSink(), new HwmPoller(store, Options),
            [], Microsoft.Extensions.Options.Options.Create(new ActaOptions { Daemon = Options }),
            NullLogger<ProjectionDaemon>.Instance, null!, NewGapGuard()))
            .Should().Throw<ArgumentNullException>().WithParameterName("deadLetters");
    }

    [Fact]
    public void ProjectionDaemon_NullGapGuard_Throws()
    {
        var store = new InMemoryEventStore();
        Invoking(() => new ProjectionDaemon(
            new InMemorySubscriptionSource(store), new InMemoryCheckpointSink(), new HwmPoller(store, Options),
            [], Microsoft.Extensions.Options.Options.Create(new ActaOptions { Daemon = Options }),
            NullLogger<ProjectionDaemon>.Instance, new DeadLetterBuffer(), null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("gapGuard");
    }

    // ── GapGuard: options / metrics / logger required ─────────────────────────────────────────────

    [Fact]
    public void GapGuard_NullOptions_Throws()
        => Invoking(() => new GapGuard(null!, new ProjectionDaemonMetrics(), NullLogger<GapGuard>.Instance))
            .Should().Throw<ArgumentNullException>().WithParameterName("options");

    [Fact]
    public void GapGuard_NullMetrics_Throws()
        => Invoking(() => new GapGuard(Options, null!, NullLogger<GapGuard>.Instance))
            .Should().Throw<ArgumentNullException>().WithParameterName("metrics");

    [Fact]
    public void GapGuard_NullLogger_Throws()
        => Invoking(() => new GapGuard(Options, new ProjectionDaemonMetrics(), null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("logger");

    [Fact]
    public void GapGuard_RecordSkip_NullOrEmptyProjectionName_Throws()
    {
        var guard = NewGapGuard();
        Invoking(() => guard.RecordSkip(null!, GlobalPosition.Start, new GlobalPosition(1)))
            .Should().Throw<ArgumentException>();
        Invoking(() => guard.RecordSkip("", GlobalPosition.Start, new GlobalPosition(1)))
            .Should().Throw<ArgumentException>();
    }

    // ── AsyncProjectionRegistration: name / eventTypes / runner required ──────────────────────────

    [Fact]
    public void Registration_NullOrEmptyName_Throws()
    {
        Invoking(() => new AsyncProjectionRegistration(null!, AsyncProjectionTestKit.IncrementedOnly, NewRunner()))
            .Should().Throw<ArgumentException>().WithParameterName("name");
        Invoking(() => new AsyncProjectionRegistration("", AsyncProjectionTestKit.IncrementedOnly, NewRunner()))
            .Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void Registration_NullEventTypes_Throws()
        => Invoking(() => new AsyncProjectionRegistration("counter", null!, NewRunner()))
            .Should().Throw<ArgumentNullException>().WithParameterName("eventTypes");

    [Fact]
    public void Registration_NullRunner_Throws()
        => Invoking(() => new AsyncProjectionRegistration("counter", AsyncProjectionTestKit.IncrementedOnly, null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("runner");

    [Fact]
    public void SetGapObserved_FirstObservationWins_DoesNotOverwrite()
    {
        var registration = new AsyncProjectionRegistration("counter", AsyncProjectionTestKit.IncrementedOnly, NewRunner());
        var first = DateTimeOffset.UnixEpoch;
        var later = first.AddMinutes(5);

        registration.SetGapObserved(first);
        registration.SetGapObserved(later); // must be a no-op — the first observation is the stable one

        registration.GapObservedAt.Should().Be(first);
    }

    // ── AddActaAsyncProjection DI extension: services / name / eventTypes required ─────────────────

    [Fact]
    public void AddActaAsyncProjection_NullServices_Throws()
        => Invoking(() => ((IServiceCollection)null!)
                .AddActaAsyncProjection<RecordingProjection<Incremented>>("counter", AsyncProjectionTestKit.IncrementedOnly))
            .Should().Throw<ArgumentNullException>().WithParameterName("services");

    [Fact]
    public void AddActaAsyncProjection_NullOrEmptyName_Throws()
    {
        Invoking(() => new ServiceCollection()
                .AddActaAsyncProjection<RecordingProjection<Incremented>>(null!, AsyncProjectionTestKit.IncrementedOnly))
            .Should().Throw<ArgumentException>().WithParameterName("name");
        Invoking(() => new ServiceCollection()
                .AddActaAsyncProjection<RecordingProjection<Incremented>>("", AsyncProjectionTestKit.IncrementedOnly))
            .Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void AddActaAsyncProjection_NullEventTypes_Throws()
        => Invoking(() => new ServiceCollection()
                .AddActaAsyncProjection<RecordingProjection<Incremented>>("counter", null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("eventTypes");
}
