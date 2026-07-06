using Xunit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.Projections.Daemon;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Unit tests for the gap-guard branch wired into <see cref="ProjectionDaemon.RunTickAsync"/> (task
/// 5.3): a true <see cref="GlobalPosition"/> gap reaching the safe HWM is skipped — advancing the
/// checkpoint through the same fenced CAS save path, incrementing <c>acta.projection.gaps_skipped</c>,
/// and logging a diagnostic warning — either immediately (no safe-harbor grace) or after the
/// safe-harbor window elapses; a non-matching tail (correction V-2) is never counted as a gap; and the
/// checkpoint never moves backward. A <see cref="GapSimulatingSubscriptionSource"/> forces the "true
/// hole reaching the HWM" scenario the real in-memory backend can never itself produce, while the
/// kit's real store (seeded with events) gives <c>HwmPoller</c> a genuine, non-zero HWM to trap the
/// checkpoint under.
/// </summary>
public sealed class ProjectionDaemonGapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunTickAsync_TrueGapWithNoSafeHarborGrace_SkipsImmediatelyAdvancesCheckpointRecordsMetricAndLogs()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented()); // real store head → hwm = 3
        var fakeSource = new GapSimulatingSubscriptionSource(); // HasRawEventAboveCheckpoint = false: a true hole
        var projection = new RecordingProjection<Incremented>();
        const string projectionName = "gap-skip-immediate";
        var registration = kit.Registration(projectionName, AsyncProjectionTestKit.IncrementedOnly, projection);
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero }; // no grace — immediate skip
        var logProvider = new ListLoggerProvider();
        var gapLogger = new Logger<GapGuard>(new SingleProviderLoggerFactory(logProvider));
        var gapGuard = new GapGuard(options, new ProjectionDaemonMetrics(), gapLogger);
        using var observer = new GapsSkippedCounterObserver(projectionName);
        var daemon = kit.Daemon(options, [registration], source: fakeSource, gapGuard: gapGuard);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        projection.Applied.Should().BeEmpty(); // the fake source never returns a real event to apply
        (await kit.Checkpoints.LoadAsync(projectionName, null, Ct)).Should().Be(new GlobalPosition(3));
        observer.Count.Should().Be(1);
        logProvider.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains(projectionName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTickAsync_NonMatchingTailAboveCheckpoint_ReturnsNoGap_CheckpointDoesNotAdvanceMetricNotIncremented()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented()); // hwm = 3
        var fakeSource = new GapSimulatingSubscriptionSource { HasRawEventAboveCheckpoint = true }; // non-matching tail, not a gap
        var projection = new RecordingProjection<Incremented>();
        const string projectionName = "gap-nogap-tail";
        var registration = kit.Registration(projectionName, AsyncProjectionTestKit.IncrementedOnly, projection);
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero };
        using var observer = new GapsSkippedCounterObserver(projectionName);
        var daemon = kit.Daemon(options, [registration], source: fakeSource);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        projection.Applied.Should().BeEmpty();
        (await kit.Checkpoints.LoadAsync(projectionName, null, Ct)).Should().BeNull(); // never saved — no gap to skip
        observer.Count.Should().Be(0); // V-2 tail must never be counted as a gap
    }

    [Fact]
    public async Task RunTickAsync_FreshGapThenSafeHarborElapsed_WaitsOnFirstTickThenSkipsOnLaterTick()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented()); // hwm = 3
        var fakeSource = new GapSimulatingSubscriptionSource(); // a true hole
        var projection = new RecordingProjection<Incremented>();
        const string projectionName = "gap-safe-harbor";
        var registration = kit.Registration(projectionName, AsyncProjectionTestKit.IncrementedOnly, projection);
        var fakeTime = new FakeTimeProvider();
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.FromSeconds(10) };
        using var observer = new GapsSkippedCounterObserver(projectionName);
        var daemon = kit.Daemon(options, [registration], source: fakeSource, timeProvider: fakeTime);

        var firstTickCatchUp = await daemon.RunTickAsync(Ct);
        var checkpointAfterFirstTick = await kit.Checkpoints.LoadAsync(projectionName, null, Ct);
        var metricAfterFirstTick = observer.Count;

        fakeTime.Advance(options.GapSafeHarborTimeout); // safe-harbor window fully elapsed
        var secondTickCatchUp = await daemon.RunTickAsync(Ct);

        firstTickCatchUp.Should().BeFalse();
        checkpointAfterFirstTick.Should().BeNull(); // still waiting — no advance yet
        metricAfterFirstTick.Should().Be(0);

        secondTickCatchUp.Should().BeFalse();
        (await kit.Checkpoints.LoadAsync(projectionName, null, Ct)).Should().Be(new GlobalPosition(3));
        observer.Count.Should().Be(1);
    }

    [Fact]
    public async Task RunTickAsync_RepeatedGapSkips_CheckpointAdvancesStrictlyForwardNeverBackward()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented()); // hwm = 3
        var fakeSource = new GapSimulatingSubscriptionSource();
        var projection = new RecordingProjection<Incremented>();
        const string projectionName = "gap-monotonic";
        var registration = kit.Registration(projectionName, AsyncProjectionTestKit.IncrementedOnly, projection);
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero };
        var daemon = kit.Daemon(options, [registration], source: fakeSource);

        await daemon.RunTickAsync(Ct); // true hole 0 → 3, skipped immediately
        var afterFirstSkip = await kit.Checkpoints.LoadAsync(projectionName, null, Ct);

        await kit.AppendAsync(Ct, new Incremented()); // hwm becomes 4 — still a true hole per the fake source
        await daemon.RunTickAsync(Ct); // true hole 3 → 4, skipped immediately
        var afterSecondSkip = await kit.Checkpoints.LoadAsync(projectionName, null, Ct);

        afterFirstSkip.Should().Be(new GlobalPosition(3));
        afterSecondSkip.Should().Be(new GlobalPosition(4));
        (afterSecondSkip!.Value.Value > afterFirstSkip!.Value.Value).Should().BeTrue(); // strictly forward, never backward
    }
}
