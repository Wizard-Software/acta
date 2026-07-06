using Xunit;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.Projections.Daemon;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Unit tests for <see cref="GapGuard"/> (task 5.3, ADR-001 R3): the pure <see cref="GapGuard.Evaluate"/>
/// decision — caught up or a non-matching tail (V-2) never counts as a gap; a gap whose safe-harbor
/// window is zero (or has already elapsed) is skipped immediately; a freshly-observed gap waits — and
/// the <see cref="GapGuard.RecordSkip"/> side effect: incrementing <c>acta.projection.gaps_skipped</c>
/// and logging a diagnostic warning naming the projection and gap range. <see cref="FakeTimeProvider"/>
/// drives every time-dependent case deterministically.
/// </summary>
public sealed class GapGuardTests
{
    private static GapGuard CreateGuard(ProjectionDaemonOptions? options = null, ILogger<GapGuard>? logger = null)
        => new(options ?? new ProjectionDaemonOptions(), new ProjectionDaemonMetrics(), logger ?? NullLogger<GapGuard>.Instance);

    [Fact]
    public void Evaluate_CheckpointAtOrAboveSafeHwm_ReturnsNoGap()
    {
        var guard = CreateGuard();
        var now = DateTimeOffset.UtcNow;

        var verdict = guard.Evaluate(
            checkpoint: new GlobalPosition(10),
            safeHwm: new GlobalPosition(10),
            rawEventExistsAboveCheckpoint: false,
            gapFirstObservedAt: null,
            now: now);

        verdict.Should().Be(GapVerdict.NoGap);
    }

    [Fact]
    public void Evaluate_RawEventExistsAboveCheckpoint_ReturnsNoGap()
    {
        // Correction V-2: a raw event above the checkpoint means a non-matching tail, not a hole —
        // this must never be counted as a gap, even though checkpoint < safeHwm.
        var guard = CreateGuard();
        var now = DateTimeOffset.UtcNow;

        var verdict = guard.Evaluate(
            checkpoint: new GlobalPosition(3),
            safeHwm: new GlobalPosition(10),
            rawEventExistsAboveCheckpoint: true,
            gapFirstObservedAt: null,
            now: now);

        verdict.Should().Be(GapVerdict.NoGap);
    }

    [Fact]
    public void Evaluate_ZeroSafeHarborTimeout_ReturnsSkipPermanentOnFirstObservation()
    {
        // A GapSafeHarborTimeout of zero grants no waiting grace at all — the expression of "a gap
        // already known to be older than the safe HWM's cutback is skipped immediately" for a backend
        // whose cutback is dormant (Tier-1's HwmPoller never applies VisibilityLag).
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.Zero };
        var guard = CreateGuard(options);
        var now = DateTimeOffset.UtcNow;

        var verdict = guard.Evaluate(
            checkpoint: new GlobalPosition(5),
            safeHwm: new GlobalPosition(10),
            rawEventExistsAboveCheckpoint: false,
            gapFirstObservedAt: null, // first observation — elapsed is treated as zero
            now: now);

        verdict.Should().Be(GapVerdict.SkipPermanent);
    }

    [Theory]
    [InlineData(null)] // first observation this tick — elapsed treated as zero
    [InlineData(5)]    // observed 5s ago — still inside the 10s default safe-harbor window
    public void Evaluate_FreshGapFirstObservationOrWithinSafeHarborWindow_ReturnsWaitSafeHarbor(int? secondsAgo)
    {
        var fakeTime = new FakeTimeProvider();
        var now = fakeTime.GetUtcNow();
        var guard = CreateGuard(); // default GapSafeHarborTimeout: 10 seconds
        DateTimeOffset? gapFirstObservedAt = secondsAgo is null ? null : now - TimeSpan.FromSeconds(secondsAgo.Value);

        var verdict = guard.Evaluate(
            checkpoint: new GlobalPosition(5),
            safeHwm: new GlobalPosition(10),
            rawEventExistsAboveCheckpoint: false,
            gapFirstObservedAt: gapFirstObservedAt,
            now: now);

        verdict.Should().Be(GapVerdict.WaitSafeHarbor);
    }

    [Fact]
    public void Evaluate_GapElapsedPastSafeHarborTimeout_ReturnsSkipPermanent()
    {
        var fakeTime = new FakeTimeProvider();
        var options = new ProjectionDaemonOptions { GapSafeHarborTimeout = TimeSpan.FromSeconds(10) };
        var guard = CreateGuard(options);
        var firstObserved = fakeTime.GetUtcNow();
        fakeTime.Advance(TimeSpan.FromSeconds(10)); // exactly the safe-harbor timeout — the ">=" boundary

        var verdict = guard.Evaluate(
            checkpoint: new GlobalPosition(5),
            safeHwm: new GlobalPosition(10),
            rawEventExistsAboveCheckpoint: false,
            gapFirstObservedAt: firstObserved,
            now: fakeTime.GetUtcNow());

        verdict.Should().Be(GapVerdict.SkipPermanent);
    }

    [Fact]
    public void RecordSkip_IncrementsGapsSkippedCounterAndLogsProjectionNameAndGapRange()
    {
        const string projectionName = "gap-guard-record-skip-test";
        using var observer = new GapsSkippedCounterObserver(projectionName);
        var logProvider = new ListLoggerProvider();
        var logger = new Logger<GapGuard>(new SingleProviderLoggerFactory(logProvider));
        var guard = new GapGuard(new ProjectionDaemonOptions(), new ProjectionDaemonMetrics(), logger);

        guard.RecordSkip(projectionName, new GlobalPosition(5), new GlobalPosition(10));

        observer.Count.Should().Be(1);
        logProvider.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(projectionName, StringComparison.Ordinal)
            && e.Message.Contains("(5, 10]", StringComparison.Ordinal));
    }
}
