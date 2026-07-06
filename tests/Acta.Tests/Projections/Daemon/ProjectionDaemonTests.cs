using Xunit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.Projections.Daemon;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Unit tests for <see cref="ProjectionDaemon"/> (task 5.2, steps 5-10): catch-up, the P×T → 1 skip,
/// batching, checkpoint restart, idempotency, the baseline error policy (halt one projection, the
/// daemon and others continue, no payload in the log), fence handling, and graceful stop. The tick
/// is driven directly via the internal <c>RunTickAsync</c> for determinism; graceful stop runs the
/// real <see cref="BackgroundService"/> loop with a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class ProjectionDaemonTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunTickAsync_AppendedMatchingEvents_AppliesAllInOrderAndSavesCheckpoint()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented());
        var projection = new RecordingProjection<Incremented>();
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)]);

        var catchUp = await daemon.RunTickAsync(Ct);

        projection.Applied.Select(a => a.Raw.GlobalPosition.Value).Should().Equal(1, 2, 3);
        var checkpoint = await kit.Checkpoints.LoadAsync("counter", null, Ct);
        checkpoint.Should().Be(new GlobalPosition(3));
        catchUp.Should().BeFalse();
    }

    [Fact]
    public async Task RunTickAsync_CaughtUpProjection_DoesNotReadAnotherBatch()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented());
        var counting = new CountingSubscriptionSource(kit.Source());
        var daemon = kit.Daemon(
            new ProjectionDaemonOptions(),
            [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>())],
            source: counting);

        await daemon.RunTickAsync(Ct);
        var callsAfterDrain = counting.ReadBatchCallCount;
        await daemon.RunTickAsync(Ct); // checkpoint >= hwm → skip, no ReadBatchAsync

        counting.ReadBatchCallCount.Should().Be(callsAfterDrain);
    }

    [Fact]
    public async Task RunTickAsync_MoreThanOneBatch_ReadsInBatchesOfBatchSizeAndAppliesAll()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, Enumerable.Range(0, 5).Select(object (_) => new Incremented()).ToArray());
        var counting = new CountingSubscriptionSource(kit.Source());
        var projection = new RecordingProjection<Incremented>();
        var options = new ProjectionDaemonOptions { BatchSize = 2, PendingEventsThreshold = 5000 };
        var daemon = kit.Daemon(options, [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)], source: counting);

        await daemon.RunTickAsync(Ct);

        projection.Applied.Should().HaveCount(5);
        counting.ObservedMaxCounts.Should().OnlyContain(m => m == 2);
        var checkpoint = await kit.Checkpoints.LoadAsync("counter", null, Ct);
        checkpoint.Should().Be(new GlobalPosition(5));
    }

    [Fact]
    public async Task RunTickAsync_FreshDaemonSharingSink_ResumesFromCheckpointWithoutReapplying()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented());
        var options = new ProjectionDaemonOptions();

        var first = new RecordingProjection<Incremented>();
        await kit.Daemon(options, [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, first)]).RunTickAsync(Ct);
        first.Applied.Should().HaveCount(2);

        await kit.AppendAsync(Ct, new Incremented());
        var second = new RecordingProjection<Incremented>();
        await kit.Daemon(options, [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, second)]).RunTickAsync(Ct);

        // Only the new event (position 3) — the first two are already past the loaded checkpoint.
        second.Applied.Select(a => a.Raw.GlobalPosition.Value).Should().Equal(3);
    }

    [Fact]
    public async Task RunTickAsync_RunTwice_AppliesEachEventExactlyOnce()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented());
        var projection = new RecordingProjection<Incremented>();
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)]);

        await daemon.RunTickAsync(Ct);
        await daemon.RunTickAsync(Ct);

        projection.Applied.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunTickAsync_ProjectionApplyThrows_HaltsThatProjectionOthersContinueWithoutPayloadInLog()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Decremented());
        var logProvider = new ListLoggerProvider();
        var logger = new Logger<ProjectionDaemon>(new SingleProviderLoggerFactory(logProvider));
        const string sentinel = "SECRET-PAYLOAD-DO-NOT-LOG";

        var faulting = new RecordingProjection<Incremented>
        {
            OnApply = (_, _) => throw new InvalidOperationException(sentinel),
        };
        var healthy = new RecordingProjection<Decremented>();
        var daemon = kit.Daemon(
            new ProjectionDaemonOptions(),
            [
                kit.Registration("faulting", AsyncProjectionTestKit.IncrementedOnly, faulting),
                kit.Registration("healthy", AsyncProjectionTestKit.DecrementedOnly, healthy),
            ],
            logger: logger);

        await Awaiting(() => daemon.RunTickAsync(Ct).AsTask()).Should().NotThrowAsync();

        faulting.Applied.Should().BeEmpty();
        healthy.Applied.Should().ContainSingle();
        logProvider.Entries.Should().Contain(e => e.Level == LogLevel.Error);
        logProvider.Entries.Should().NotContain(e => e.Message.Contains(sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTickAsync_PausedProjection_IsNotLedOnSubsequentTicks()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var applyCount = 0;
        var faulting = new RecordingProjection<Incremented>
        {
            OnApply = (_, _) =>
            {
                applyCount++;
                throw new InvalidOperationException("boom");
            },
        };
        // Pause with MaxRetries 0 == the 5.2 baseline halt-on-first-failure: task 5.4 moved that
        // semantics under the ErrorAction.Pause policy (the default is now DeadLetterAndSkip).
        var pauseImmediately = new ProjectionErrorPolicy(ErrorAction.Pause, MaxRetries: 0);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [kit.Registration("faulting", AsyncProjectionTestKit.IncrementedOnly, faulting, pauseImmediately)]);

        await daemon.RunTickAsync(Ct);
        await daemon.RunTickAsync(Ct);

        applyCount.Should().Be(1); // paused after the first failure — never attempted again
    }

    [Fact]
    public async Task RunTickAsync_FencingSink_HaltsProjectionAndStopsLeadingItAfterFence()
    {
        // A fenced checkpoint save means this daemon lost leadership; its fixed per-instance owner
        // token is now permanently stale. The zombie-guard must STOP leading the projection — not
        // re-lead it every tick, re-applying events and re-fencing forever with the same stale token.
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var fencing = new FencingCheckpointSink();
        var projection = new RecordingProjection<Incremented>();
        var registration = kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration], checkpoints: fencing);

        // First tick: apply the event, the checkpoint save fences → the projection is abandoned.
        await Awaiting(() => daemon.RunTickAsync(Ct).AsTask()).Should().NotThrowAsync();
        var savesAfterFence = fencing.SaveCallCount;
        // Second tick: the halted projection must NOT be led again.
        await Awaiting(() => daemon.RunTickAsync(Ct).AsTask()).Should().NotThrowAsync();

        fencing.SaveCallCount.Should().BeGreaterThanOrEqualTo(1); // the first save fenced
        registration.IsHalted.Should().BeTrue();                  // stopped leading it (zombie-guard)
        fencing.SaveCallCount.Should().Be(savesAfterFence);       // no further fenced save on the next tick
        projection.Applied.Should().HaveCount(1);                 // event never re-applied under the stale token
    }

    [Fact]
    public async Task ExecuteAsync_StartThenStop_FinishesBatchSavesCheckpointAndStopsCleanly()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented());
        var fakeTime = new FakeTimeProvider();
        var projection = new RecordingProjection<Incremented>();
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signaling = new SignalingCheckpointSink(kit.Checkpoints, new GlobalPosition(2), saved);
        var daemon = kit.Daemon(
            new ProjectionDaemonOptions { PollingInterval = TimeSpan.FromMilliseconds(50) },
            [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)],
            checkpoints: signaling,
            timeProvider: fakeTime);

        await daemon.StartAsync(Ct);
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await daemon.StopAsync(Ct);

        projection.Applied.Should().HaveCount(2);
        var checkpoint = await kit.Checkpoints.LoadAsync("counter", null, Ct);
        checkpoint.Should().Be(new GlobalPosition(2));
    }

    /// <summary>Decorator that forwards to an inner sink and completes <paramref name="saved"/> once the checkpoint reaches <paramref name="target"/>.</summary>
    private sealed class SignalingCheckpointSink(ICheckpointSink inner, GlobalPosition target, TaskCompletionSource saved) : ICheckpointSink
    {
        public ValueTask<GlobalPosition?> LoadAsync(string projectionName, string? tenantId, CancellationToken ct = default)
            => inner.LoadAsync(projectionName, tenantId, ct);

        public async ValueTask SaveAsync(string projectionName, string? tenantId, GlobalPosition position, string ownerToken, CancellationToken ct = default)
        {
            await inner.SaveAsync(projectionName, tenantId, position, ownerToken, ct);
            if (position >= target)
            {
                saved.TrySetResult();
            }
        }
    }
}
