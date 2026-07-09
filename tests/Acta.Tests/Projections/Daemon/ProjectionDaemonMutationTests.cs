using Xunit;

using Microsoft.Extensions.Logging;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.Projections.Daemon;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Additional mutation-kill coverage for <see cref="ProjectionDaemon"/> beyond the existing
/// <c>ProjectionDaemonTests</c> / <c>ProjectionDaemonGapTests</c> / <c>ProjectionDaemonErrorPolicyTests</c>
/// / <c>ProjectionDaemonBackpressureTests</c> suites: the "trust the cached checkpoint" load
/// short-circuit, the per-tick lag-snapshot publish, the raw-peek "one element is enough" guard, every
/// branch's own <c>ExitCatchUp</c>/<c>ClearGapObserved</c>/<c>CacheCheckpoint</c> call (proven via a
/// deliberately stale pre-tick flag rather than relying on the default already matching), both
/// fenced-save zombie-guards' full effect (not just <c>IsHalted</c>), the "nothing advanced → no save"
/// guard, the paused-projection catch-up-state ordering, the partial-batch single-read guarantee, the
/// full-batch-under-threshold path's own <c>ExitCatchUp</c>, cooperative cancellation's rethrow inside
/// the error policy, and the Pause/Skip error-policy log lines' actual rendered text (not just their
/// level). Per ADR-014 the daemon is single-threaded on checkpoint mutation — every scenario here is
/// still driven directly through the internal <c>RunTickAsync</c>, deterministically, no background
/// thread.
/// </summary>
public sealed class ProjectionDaemonMutationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Trust the cached checkpoint; load only at first lead (line 205) ──────────────────────────

    [Fact]
    public async Task RunTickAsync_SecondTickWithCachedCheckpoint_DoesNotReloadFromTheSink()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var countingSink = new CountingCheckpointSink(kit.Checkpoints);
        var registration = kit.Registration("cached-checkpoint", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration], checkpoints: countingSink);

        await daemon.RunTickAsync(Ct); // first lead: no cache yet → must load once
        var loadsAfterFirstTick = countingSink.LoadCallCount;
        await kit.AppendAsync(Ct, new Incremented());
        await daemon.RunTickAsync(Ct); // cache is now populated → must NOT reload

        loadsAfterFirstTick.Should().Be(1);
        countingSink.LoadCallCount.Should().Be(loadsAfterFirstTick); // unchanged — the cache was trusted
    }

    // ── Per-tick lag snapshot publish (line 212) ──────────────────────────────────────────────────

    [Fact]
    public async Task RunTickAsync_PublishesLagSnapshotBeforeApplyingTheBatch()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented());
        var registration = kit.Registration("lag-snapshot", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration]);

        await daemon.RunTickAsync(Ct);

        // Snapshot is taken from the checkpoint AS IT STOOD AT THE START of this tick (Start, i.e. 0)
        // and the hwm shared across the tick (2) — never left at their zero defaults.
        registration.HwmSnapshot.Should().Be(2);
        registration.CheckpointSnapshot.Should().Be(0);
    }

    // ── Raw peek stops after the first element (line 237) ─────────────────────────────────────────

    [Fact]
    public async Task RunTickAsync_MultipleRawEventsAboveCheckpoint_PeeksOnlyTheFirstThenStops()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented()); // real store head → hwm = 3
        var rawEvents = new[]
        {
            CountingRawPeekSource.SyntheticEvent(1),
            CountingRawPeekSource.SyntheticEvent(2),
            CountingRawPeekSource.SyntheticEvent(3),
        };
        var peekSource = new CountingRawPeekSource(rawEvents);
        var registration = kit.Registration("raw-peek-once", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration], source: peekSource);

        await daemon.RunTickAsync(Ct);

        peekSource.YieldedCount.Should().Be(1); // existence, not content, is all the guard needs
    }

    // ── WaitSafeHarbor exits catch-up even if a stale flag was still set (line 246) ──────────────

    [Fact]
    public async Task RunTickAsync_GapWithinSafeHarborWindow_ExitsCatchUpEvenIfPreviouslyInCatchUp()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented());
        var fakeSource = new GapSimulatingSubscriptionSource(); // true hole
        var registration = kit.Registration("waitharbor-exits-catchup", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        registration.EnterCatchUp(); // stale flag from a previous tick's backlog handling
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.FromSeconds(10) }; // not yet elapsed
        var daemon = kit.Daemon(options, [registration], source: fakeSource);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        registration.IsInCatchUp.Should().BeFalse();
    }

    // ── SkipPermanent + fencing sink: full zombie-guard effect (lines 266-268) ────────────────────

    [Fact]
    public async Task RunTickAsync_SkipPermanentGapWithFencingSink_DropsLeadershipAndExitsCatchUp()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented());
        var fakeSource = new GapSimulatingSubscriptionSource();
        var fencing = new FencingCheckpointSink();
        var registration = kit.Registration("gap-skip-fenced", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        registration.EnterCatchUp(); // stale flag — must be cleared by this same branch
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero }; // immediate SkipPermanent
        var daemon = kit.Daemon(options, [registration], source: fakeSource, checkpoints: fencing);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        registration.IsHalted.Should().BeTrue();          // DropLeadership
        registration.IsInCatchUp.Should().BeFalse();      // ExitCatchUp
        registration.CachedCheckpoint.Should().BeNull();  // DropLeadership clears the cache; nothing re-caches it after
        fencing.SaveCallCount.Should().Be(1);
    }

    // ── SkipPermanent success path: caches the new checkpoint (line 271) ──────────────────────────

    [Fact]
    public async Task RunTickAsync_SkipPermanentGap_CachesTheAdvancedCheckpoint()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented());
        var fakeSource = new GapSimulatingSubscriptionSource();
        var registration = kit.Registration("gap-caches-checkpoint", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero };
        var daemon = kit.Daemon(options, [registration], source: fakeSource);

        await daemon.RunTickAsync(Ct);

        registration.CachedCheckpoint.Should().Be(new GlobalPosition(3));
    }

    // ── SkipPermanent success path: clears a stale gap-observed timestamp (line 273) ─────────────

    [Fact]
    public async Task RunTickAsync_SkipPermanentGap_ClearsAStaleGapObservedTimestamp()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented());
        var fakeSource = new GapSimulatingSubscriptionSource();
        var registration = kit.Registration("gap-clears-observed", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        registration.SetGapObserved(DateTimeOffset.UnixEpoch); // stale — from an earlier, now-resolved gap window
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero };
        var daemon = kit.Daemon(options, [registration], source: fakeSource);

        await daemon.RunTickAsync(Ct);

        registration.GapObservedAt.Should().BeNull();
    }

    // ── SkipPermanent success path: exits catch-up even if previously set (line 274) ─────────────

    [Fact]
    public async Task RunTickAsync_SkipPermanentGap_ExitsCatchUpEvenIfPreviouslyInCatchUp()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented());
        var fakeSource = new GapSimulatingSubscriptionSource();
        var registration = kit.Registration("gap-exits-catchup", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        registration.EnterCatchUp();
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero };
        var daemon = kit.Daemon(options, [registration], source: fakeSource);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        registration.IsInCatchUp.Should().BeFalse();
    }

    // ── NoGap (non-matching tail) clears a stale gap-observed timestamp (line 279) ────────────────

    [Fact]
    public async Task RunTickAsync_NonMatchingTailNoGap_ClearsAStaleGapObservedTimestamp()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented());
        var fakeSource = new GapSimulatingSubscriptionSource { HasRawEventAboveCheckpoint = true }; // non-matching tail → NoGap
        var registration = kit.Registration("nogap-clears-observed", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        registration.SetGapObserved(DateTimeOffset.UnixEpoch);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration], source: fakeSource);

        await daemon.RunTickAsync(Ct);

        registration.GapObservedAt.Should().BeNull();
    }

    // ── Paused: does not enter catch-up mode after a poisoned "full" batch (lines 294, 329) ──────

    [Fact]
    public async Task RunTickAsync_PausedProjection_DoesNotEnterCatchUpModeAfterAPoisonedFullBatch()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented()); // exactly 1 event
        var faulting = new RecordingProjection<Incremented> { OnApply = (_, _) => throw new InvalidOperationException("poison") };
        var pause = new ProjectionErrorPolicy(ErrorAction.Pause, MaxRetries: 0);
        // BatchSize = 1 so the single poisoned event IS a "full" batch, and PendingEventsThreshold = 0
        // so any remaining pending (the checkpoint stuck behind the poison) would trip EnterCatchUp
        // if the "if (paused)" early-exit were skipped.
        var options = new ProjectionDaemonOptions { BatchSize = 1, PendingEventsThreshold = 0 };
        var registration = kit.Registration("pause-no-catchup", AsyncProjectionTestKit.IncrementedOnly, faulting, pause);
        var daemon = kit.Daemon(options, [registration]);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        registration.IsInCatchUp.Should().BeFalse();
        registration.IsHalted.Should().BeTrue();
    }

    // ── Paused: exits catch-up mode even if previously set (line 328) ────────────────────────────

    [Fact]
    public async Task RunTickAsync_PausedProjection_ExitsCatchUpModeEvenIfPreviouslyInCatchUp()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var faulting = new RecordingProjection<Incremented> { OnApply = (_, _) => throw new InvalidOperationException("poison") };
        var pause = new ProjectionErrorPolicy(ErrorAction.Pause, MaxRetries: 0);
        var registration = kit.Registration("pause-exits-stale-catchup", AsyncProjectionTestKit.IncrementedOnly, faulting, pause);
        registration.EnterCatchUp();
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration]);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        registration.IsInCatchUp.Should().BeFalse();
    }

    // ── Nothing advanced this batch → no redundant checkpoint save (line 302, > vs >=) ────────────

    [Fact]
    public async Task RunTickAsync_FirstEventPausedImmediately_NeverSavesTheUnchangedCheckpoint()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var faulting = new RecordingProjection<Incremented> { OnApply = (_, _) => throw new InvalidOperationException("poison") };
        var pause = new ProjectionErrorPolicy(ErrorAction.Pause, MaxRetries: 0);
        var countingSink = new CountingCheckpointSink(kit.Checkpoints);
        var registration = kit.Registration("no-advance-no-save", AsyncProjectionTestKit.IncrementedOnly, faulting, pause);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration], checkpoints: countingSink);

        await daemon.RunTickAsync(Ct);

        countingSink.SaveCallCount.Should().Be(0); // lastGood == checkpoint (nothing applied) — no save needed
    }

    // ── Normal-batch fenced save: full zombie-guard effect (lines 319, 320) ───────────────────────

    [Fact]
    public async Task RunTickAsync_FencedSaveDuringNormalBatchApply_ClearsCachedCheckpointAndExitsCatchUp()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var fencing = new FencingCheckpointSink();
        var registration = kit.Registration("fence-clears-cache", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        registration.EnterCatchUp(); // stale flag — must be cleared by this same branch
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration], checkpoints: fencing);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        registration.IsInCatchUp.Should().BeFalse();
        // DropLeadership clears CachedCheckpoint; the `break` must fire BEFORE the unconditional
        // CacheCheckpoint(checkpoint) a few lines below, or that clear would be immediately undone.
        registration.CachedCheckpoint.Should().BeNull();
    }

    // ── Partial batch: exits catch-up even if previously set (line 334) ──────────────────────────

    [Fact]
    public async Task RunTickAsync_PartialBatch_ExitsCatchUpModeEvenIfPreviouslyInCatchUp()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented());
        var registration = kit.Registration("partial-batch-exits-catchup", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        registration.EnterCatchUp();
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration]);

        var catchUp = await daemon.RunTickAsync(Ct);

        catchUp.Should().BeFalse();
        registration.IsInCatchUp.Should().BeFalse();
    }

    // ── Partial batch: reads exactly once this tick, never a second (empty) batch (line 335) ─────

    [Fact]
    public async Task RunTickAsync_PartialBatchFullyDrains_ReadsExactlyOneBatchThisTick()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented());
        var counting = new CountingSubscriptionSource(kit.Source());
        var registration = kit.Registration("partial-drain-once", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration], source: counting);

        await daemon.RunTickAsync(Ct);

        counting.ReadBatchCallCount.Should().Be(1); // must break immediately, not loop for another (empty) read
    }

    // ── Full batch under threshold: exits catch-up before the loop's next iteration (line 347) ───

    [Fact]
    public async Task RunTickAsync_FullBatchUnderThreshold_ExitsCatchUpModeEvenIfPreviouslyInCatchUp()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented(), new Incremented()); // 3 events; hwm = 3
        using var cts = new CancellationTokenSource();
        // BatchSize = 3 so the first (only) read is a FULL batch (Count == BatchSize, skipping the
        // partial-batch exit at line 334), and PendingEventsThreshold is high enough that pending (0)
        // never trips EnterCatchUp — forcing the "full batch under threshold" fall-through at line
        // 347. The sink cancels right after its save completes, so the loop's *next* top-of-while
        // check exits gracefully (no exception) instead of reading another (now-empty) batch.
        var options = new ProjectionDaemonOptions { BatchSize = 3, PendingEventsThreshold = 5000 };
        var registration = kit.Registration("full-batch-under-threshold", AsyncProjectionTestKit.IncrementedOnly, new RecordingProjection<Incremented>());
        registration.EnterCatchUp(); // stale flag — must be cleared by line 347, not by a later iteration
        var cancelingSink = new CancelAfterSaveCheckpointSink(kit.Checkpoints, cts);
        var daemon = kit.Daemon(options, [registration], checkpoints: cancelingSink);

        await daemon.RunTickAsync(cts.Token);

        registration.IsInCatchUp.Should().BeFalse();
    }

    // ── Cancellation during apply propagates without retrying or dead-lettering (line 393) ───────

    [Fact]
    public async Task RunTickAsync_ApplyThrowsOperationCanceledExceptionOnce_PropagatesWithoutRetrying()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var attempts = 0;
        var projection = new RecordingProjection<Incremented>
        {
            OnApply = (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new OperationCanceledException("graceful stop mid-apply");
                }

                return ValueTask.CompletedTask;
            },
        };
        var registration = kit.Registration("cancel-mid-apply", AsyncProjectionTestKit.IncrementedOnly, projection);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration]);

        // If the daemon swallowed and retried instead of rethrowing, this would complete normally
        // (attempts == 2, no exception) rather than throw on the very first attempt.
        await Awaiting(() => daemon.RunTickAsync(Ct).AsTask()).Should().ThrowAsync<OperationCanceledException>();

        attempts.Should().Be(1);
        kit.DeadLetters.Snapshot().Should().BeEmpty(); // not an apply error — never reaches the dead-letter policy
    }

    // ── Pause / Skip error-policy log lines: actual rendered text, not just the level (417, 426) ─

    [Fact]
    public async Task RunTickAsync_PersistentFailure_Pause_LogsProjectionNameAndPausedText()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var logProvider = new ListLoggerProvider();
        var logger = new Logger<ProjectionDaemon>(new SingleProviderLoggerFactory(logProvider));
        var projection = new RecordingProjection<Incremented> { OnApply = (_, _) => throw new InvalidOperationException("poison") };
        var pause = new ProjectionErrorPolicy(ErrorAction.Pause, MaxRetries: 0);
        var registration = kit.Registration("pause-logs", AsyncProjectionTestKit.IncrementedOnly, projection, pause);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration], logger: logger);

        await daemon.RunTickAsync(Ct);

        logProvider.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error
            && e.Message.Contains("pause-logs", StringComparison.Ordinal)
            && e.Message.Contains("paused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTickAsync_PersistentFailure_DeadLetterAndSkip_LogsProjectionNameAndDeadLetteredText()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var logProvider = new ListLoggerProvider();
        var logger = new Logger<ProjectionDaemon>(new SingleProviderLoggerFactory(logProvider));
        var projection = new RecordingProjection<Incremented> { OnApply = (_, _) => throw new InvalidOperationException("poison") };
        var registration = kit.Registration("skip-logs", AsyncProjectionTestKit.IncrementedOnly, projection); // default policy: DeadLetterAndSkip
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration], logger: logger);

        await daemon.RunTickAsync(Ct);

        logProvider.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error
            && e.Message.Contains("skip-logs", StringComparison.Ordinal)
            && e.Message.Contains("dead-lettered", StringComparison.Ordinal));
    }
}
