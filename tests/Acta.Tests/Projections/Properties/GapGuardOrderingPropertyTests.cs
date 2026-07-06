using CsCheck;
using Xunit;

using Microsoft.Extensions.Logging.Abstractions;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.Projections.Daemon;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections.Properties;

/// <summary>
/// Property tests for the "Kolejność / monotoniczność" row of TESTING-SPEC §6.1 (task 5.5 — NR3 part 2),
/// exercising the ordering / gap side of the row: the pure <see cref="GapGuard.Evaluate"/> decision over
/// generated <see cref="GlobalPosition"/> sequences with gaps (single / serial / end-of-batch), and the
/// forward-only checkpoint advance observed across a series of gap skips at the
/// <see cref="ProjectionDaemon.RunTickAsync"/> level. The checkpoint monotonicity contract of
/// <see cref="ICheckpointSink"/> (duplicate position after retry; never backward) is covered in its own
/// class alongside this one.
/// <para>
/// <see cref="GapGuard.Evaluate"/> and <see cref="GapVerdict"/> are <see langword="internal"/>, reached
/// via <c>InternalsVisibleTo("Acta.Tests")</c> exactly as <see cref="GapGuardTests"/> does. Each daemon
/// property iteration builds its OWN <see cref="AsyncProjectionTestKit"/> — CsCheck runs samples in
/// parallel, so a shared daemon/store would race and leak state across iterations.
/// </para>
/// </summary>
public sealed class GapGuardOrderingPropertyTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static GapGuard CreateGuard(ProjectionDaemonOptions? options = null)
        => new(options ?? new ProjectionDaemonOptions(), new ProjectionDaemonMetrics(), NullLogger<GapGuard>.Instance);

    // ---- P1: caught up — checkpoint at or above the safe HWM is never a gap ----

    [Fact]
    public void Property_CaughtUp_AlwaysNoGap()
    {
        var gen =
            from safeHwm in Gen.Int[0, 100]
            from extra in Gen.Int[0, 50]
            from rawEvent in Gen.Bool
            from hasObserved in Gen.Bool
            select (safeHwm, extra, rawEvent, hasObserved);

        gen.Sample(t =>
        {
            var guard = CreateGuard();
            var checkpoint = new GlobalPosition(t.safeHwm + t.extra); // checkpoint >= safeHwm
            DateTimeOffset? observed = t.hasObserved ? BaseTime : null;

            guard.Evaluate(checkpoint, new GlobalPosition(t.safeHwm), t.rawEvent, observed, BaseTime)
                .Should().Be(GapVerdict.NoGap);
        });
    }

    // ---- P2: a non-matching tail reaching the HWM is never counted as a gap (correction V-2) ----

    [Fact]
    public void Property_NonMatchingTailReachingHwm_AlwaysNoGap()
    {
        GlobalPositionSequenceGenerators.GapReachingHwm.Sample(pair =>
        {
            // Zero safe-harbor grace would otherwise skip immediately — a raw event above the
            // checkpoint must still classify as NoGap (a filter miss, not a hole).
            var guard = CreateGuard(new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero });

            guard.Evaluate(pair.Checkpoint, pair.SafeHwm, rawEventExistsAboveCheckpoint: true, null, BaseTime)
                .Should().Be(GapVerdict.NoGap);
        });
    }

    // ---- P3: a true hole reaching the HWM with no grace is skipped immediately, whatever its shape ----

    [Fact]
    public void Property_TrueHoleZeroSafeHarbor_AlwaysSkipPermanent()
    {
        // Every generated ascending-with-gaps sequence is a hole reaching the HWM when read from Start:
        // single, serial, and end-of-batch gap shapes are all in the sample space.
        GlobalPositionSequenceGenerators.AscendingWithGaps.Sample(sequence =>
        {
            var guard = CreateGuard(new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero });
            var safeHwm = sequence[^1];

            guard.Evaluate(GlobalPosition.Start, safeHwm, rawEventExistsAboveCheckpoint: false, null, BaseTime)
                .Should().Be(GapVerdict.SkipPermanent);
        });
    }

    // ---- P4: within the safe-harbor window a true hole waits, past it (>=) it skips ----

    [Fact]
    public void Property_TrueHoleWithinSafeHarbor_WaitsThenSkipsWhenElapsed()
    {
        var gen =
            from pair in GlobalPositionSequenceGenerators.GapReachingHwm
            from timeoutSec in Gen.Int[1, 60]
            from elapsedSec in Gen.Int[0, 120]
            select (pair, timeoutSec, elapsedSec);

        gen.Sample(t =>
        {
            var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.FromSeconds(t.timeoutSec) };
            var guard = CreateGuard(options);
            var now = BaseTime + TimeSpan.FromSeconds(t.elapsedSec);

            var verdict = guard.Evaluate(t.pair.Checkpoint, t.pair.SafeHwm, false, BaseTime, now);

            var expected = t.elapsedSec >= t.timeoutSec ? GapVerdict.SkipPermanent : GapVerdict.WaitSafeHarbor;
            verdict.Should().Be(expected);
        });
    }

    // ---- P5: Evaluate is total (never throws) and deterministic over any input combination ----

    [Fact]
    public void Property_Evaluate_IsTotalAndDeterministic()
    {
        var gen =
            from checkpoint in Gen.Int[0, 100]
            from safeHwm in Gen.Int[0, 100]
            from rawEvent in Gen.Bool
            from hasObserved in Gen.Bool
            from observedOffset in Gen.Int[-100, 100]
            from timeoutSec in Gen.Int[0, 60]
            select (checkpoint, safeHwm, rawEvent, hasObserved, observedOffset, timeoutSec);

        gen.Sample(t =>
        {
            var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.FromSeconds(t.timeoutSec) };
            var guard = CreateGuard(options);
            DateTimeOffset? observed = t.hasObserved ? BaseTime + TimeSpan.FromSeconds(t.observedOffset) : null;

            // Two calls with identical inputs: never throws (totality) and yields the same verdict.
            var first = guard.Evaluate(new GlobalPosition(t.checkpoint), new GlobalPosition(t.safeHwm), t.rawEvent, observed, BaseTime);
            var second = guard.Evaluate(new GlobalPosition(t.checkpoint), new GlobalPosition(t.safeHwm), t.rawEvent, observed, BaseTime);

            first.Should().Be(second);
            first.Should().BeOneOf(GapVerdict.NoGap, GapVerdict.SkipPermanent, GapVerdict.WaitSafeHarbor);
        });
    }

    // ---- Explicit gap-shape facts — auditable mapping to the §6.1 wording ----

    [Fact]
    public void Evaluate_SingleGapReachingHwm_ZeroSafeHarbor_SkipsPermanent()
    {
        // Sequence 1,2,4,5 — a SINGLE gap (position 3 missing); checkpoint below, safe HWM at the top.
        AssertImmediateSkip(GlobalPosition.Start, new GlobalPosition(5));
    }

    [Fact]
    public void Evaluate_SerialGapsReachingHwm_ZeroSafeHarbor_SkipsPermanent()
    {
        // Sequence 1,5,6 — a SERIAL gap (positions 2,3,4 all missing).
        AssertImmediateSkip(GlobalPosition.Start, new GlobalPosition(6));
    }

    [Fact]
    public void Evaluate_GapAtEndOfBatch_ZeroSafeHarbor_SkipsPermanent()
    {
        // Consumed up to position 3; the safe HWM sits at 7 — the gap (4..7) is at the END of the batch.
        AssertImmediateSkip(new GlobalPosition(3), new GlobalPosition(7));
    }

    private static void AssertImmediateSkip(GlobalPosition checkpoint, GlobalPosition safeHwm)
    {
        var guard = CreateGuard(new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero });

        guard.Evaluate(checkpoint, safeHwm, rawEventExistsAboveCheckpoint: false, null, BaseTime)
            .Should().Be(GapVerdict.SkipPermanent);
    }

    // ---- P6: across repeated gap-skip ticks the daemon's checkpoint never moves backward ----

    [Fact]
    public async Task Property_RepeatedGapSkipTicks_CheckpointNeverGoesBackward()
    {
        var ct = TestContext.Current.CancellationToken;

        await GlobalPositionSequenceGenerators.PositiveAppendBatches.SampleAsync(async batches =>
        {
            const string projectionName = "gap-monotonic-prop";
            var kit = new AsyncProjectionTestKit();
            var fakeSource = new GapSimulatingSubscriptionSource(); // a true hole reaching the HWM
            var projection = new RecordingProjection<Incremented>();
            var registration = kit.Registration(projectionName, AsyncProjectionTestKit.IncrementedOnly, projection);
            var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero }; // immediate skip
            var daemon = kit.Daemon(options, [registration], source: fakeSource);

            long previous = -1;
            long total = 0;
            foreach (var batchSize in batches)
            {
                var events = new object[batchSize];
                for (var i = 0; i < batchSize; i++)
                {
                    events[i] = new Incremented();
                }

                await kit.AppendAsync(ct, events);
                total += batchSize;

                await daemon.RunTickAsync(ct);
                var checkpoint = await kit.Checkpoints.LoadAsync(projectionName, null, ct);

                checkpoint.Should().NotBeNull();
                checkpoint!.Value.Value.Should().BeGreaterThanOrEqualTo(previous); // never backward
                previous = checkpoint.Value.Value;
            }

            previous.Should().Be(total); // final checkpoint == cumulative safe HWM after every skip
        });
    }
}
