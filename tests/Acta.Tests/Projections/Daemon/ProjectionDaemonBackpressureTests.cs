using Xunit;

using Acta.Configuration;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Backpressure / catch-up unit tests for <see cref="Acta.Projections.Daemon.ProjectionDaemon"/>
/// (task 5.2, step 8) — including the mandatory correction V-2: a type-selective projection whose
/// checkpoint can never reach the global head (trailing events are non-matching) MUST leave catch-up
/// mode once its matching backlog is drained, so the daemon does not busy-spin. The tick return
/// value (any projection in catch-up → skip the polling delay) is asserted directly for determinism.
/// </summary>
public sealed class ProjectionDaemonBackpressureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object[] Incrementing(int count)
        => [.. Enumerable.Range(0, count).Select(object (_) => new Incremented())];

    [Fact]
    public async Task RunTickAsync_PendingBacklogAboveThreshold_SignalsCatchUpAndYieldsAfterOneBatch()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, Incrementing(5));
        var projection = new RecordingProjection<Incremented>();
        var options = new ProjectionDaemonOptions { BatchSize = 2, PendingEventsThreshold = 1 };
        var daemon = kit.Daemon(options, [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)]);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeTrue();               // pending (5-2=3) > threshold (1) after the first full batch
        projection.Applied.Should().HaveCount(2); // one batch this tick, then yield to the loop
    }

    [Fact]
    public async Task RunTickAsync_RepeatedTicks_DrainBacklogThenReturnToNormalMode()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, Incrementing(5));
        var projection = new RecordingProjection<Incremented>();
        var options = new ProjectionDaemonOptions { BatchSize = 2, PendingEventsThreshold = 1 };
        var daemon = kit.Daemon(options, [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)]);

        var first = await daemon.RunTickAsync(Ct);
        var second = await daemon.RunTickAsync(Ct);

        first.Should().BeTrue();   // backlog above threshold
        second.Should().BeFalse(); // drained → polling delay resumes
        projection.Applied.Should().HaveCount(5);
    }

    [Fact]
    public async Task RunTickAsync_BacklogBelowThreshold_DrainsInOneTickAndSignalsNormalMode()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, Incrementing(3));
        var projection = new RecordingProjection<Incremented>();
        var options = new ProjectionDaemonOptions { BatchSize = 2, PendingEventsThreshold = 5000 };
        var daemon = kit.Daemon(options, [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)]);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        projection.Applied.Should().HaveCount(3);
    }

    [Fact]
    public async Task RunTickAsync_SelectiveProjectionCaughtUp_ExitsCatchUpModeNoBusySpin()
    {
        // V-2 regression: a projection filtered to Incremented, whose matching events are followed by
        // non-matching Decremented events, keeps its checkpoint below the global head forever. Naive
        // backpressure (pending = hwm - checkpoint > threshold) would wedge the catch-up flag on and
        // busy-spin the daemon. The flag MUST clear once the matching backlog is exhausted.
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(
            Ct,
            new Incremented(), new Incremented(),
            new Decremented(), new Decremented(), new Decremented(), new Decremented());
        var projection = new RecordingProjection<Incremented>();
        var options = new ProjectionDaemonOptions { BatchSize = 2, PendingEventsThreshold = 1 };
        var daemon = kit.Daemon(options, [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)]);

        var first = await daemon.RunTickAsync(Ct);  // full matching batch, pending (6-2=4) > 1 → catch-up
        var second = await daemon.RunTickAsync(Ct); // no more matching events up to the HWM → caught up

        first.Should().BeTrue();
        second.Should().BeFalse();                  // catch-up flag cleared — no busy-spin
        projection.Applied.Should().HaveCount(2);   // only the two Incremented events, never the Decremented
    }

    [Fact]
    public async Task RunTickAsync_SelectiveProjectionWithNoMatchingEvents_StaysInNormalMode()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Decremented(), new Decremented(), new Decremented());
        var projection = new RecordingProjection<Incremented>();
        var options = new ProjectionDaemonOptions { BatchSize = 2, PendingEventsThreshold = 1 };
        var daemon = kit.Daemon(options, [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)]);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        projection.Applied.Should().BeEmpty();
    }
}
