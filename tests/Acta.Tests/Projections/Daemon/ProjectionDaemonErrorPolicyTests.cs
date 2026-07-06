using Xunit;

using Microsoft.Extensions.Logging;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.Projections.Daemon;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Unit tests for the per-projection error policy (task 5.4): retry within <c>MaxRetries</c> recovers a
/// transient failure without a dead-letter; a persistent failure is dead-lettered then either skipped
/// (<see cref="ErrorAction.DeadLetterAndSkip"/> — the projection and daemon keep running, checkpoint
/// advances past the poison) or paused (<see cref="ErrorAction.Pause"/> — that one projection halts, its
/// checkpoint stops before the poison); one poisoned event never stops the daemon or a sibling
/// projection; the recorded <c>Error</c> carries only the exception type + message (no library-added
/// payload) and the log line does not inline the exception text; and the attempt count is
/// <c>MaxRetries + 1</c>. Driven directly via the internal <c>RunTickAsync</c> for determinism.
/// </summary>
public sealed class ProjectionDaemonErrorPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunTickAsync_TransientFailureWithinMaxRetries_RecoversAndRecordsNoDeadLetter()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var attempts = 0;
        var projection = new RecordingProjection<Incremented>
        {
            OnApply = (_, _) => ++attempts <= 2
                ? throw new InvalidOperationException("transient")
                : ValueTask.CompletedTask,
        };
        // Default policy: DeadLetterAndSkip, MaxRetries 3 — two transient failures are absorbed by retries.
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)]);

        await daemon.RunTickAsync(Ct);

        attempts.Should().Be(3); // 2 failures + 1 success
        projection.Applied.Should().ContainSingle();
        kit.DeadLetters.Snapshot().Should().BeEmpty();
        (await kit.Checkpoints.LoadAsync("counter", null, Ct)).Should().Be(new GlobalPosition(1));
    }

    [Fact]
    public async Task RunTickAsync_PersistentFailure_DeadLetterAndSkip_RecordsPoisonSkipsItAndContinues()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented()); // positions 1 (poison), 2 (healthy)
        var projection = new RecordingProjection<Incremented>
        {
            OnApply = (_, raw) => raw.GlobalPosition == new GlobalPosition(1)
                ? throw new InvalidOperationException("poison")
                : ValueTask.CompletedTask,
        };
        var registration = kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration]);

        await daemon.RunTickAsync(Ct);

        // Position 1 skipped, position 2 applied; the projection keeps running (not halted).
        projection.Applied.Select(a => a.Raw.GlobalPosition.Value).Should().Equal(2);
        registration.IsHalted.Should().BeFalse();
        (await kit.Checkpoints.LoadAsync("counter", null, Ct)).Should().Be(new GlobalPosition(2));

        var entry = kit.DeadLetters.Snapshot().Should().ContainSingle().Subject;
        entry.ProjectionName.Should().Be("counter");
        entry.Position.Should().Be(new GlobalPosition(1));
        entry.Attempts.Should().Be(4); // initial + 3 retries
    }

    [Fact]
    public async Task RunTickAsync_PersistentFailure_Pause_HaltsProjectionAndCheckpointStopsBeforePoison()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Incremented()); // positions 1 (healthy), 2 (poison)
        var projection = new RecordingProjection<Incremented>
        {
            OnApply = (_, raw) => raw.GlobalPosition == new GlobalPosition(2)
                ? throw new InvalidOperationException("poison")
                : ValueTask.CompletedTask,
        };
        var pause = new ProjectionErrorPolicy(ErrorAction.Pause);
        var registration = kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection, pause);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration]);

        await daemon.RunTickAsync(Ct);

        projection.Applied.Select(a => a.Raw.GlobalPosition.Value).Should().Equal(1);
        registration.IsHalted.Should().BeTrue();
        // Checkpoint stops at the last good position (1), NOT past the poison (2) — rebuild/resume retries it.
        (await kit.Checkpoints.LoadAsync("counter", null, Ct)).Should().Be(new GlobalPosition(1));

        var entry = kit.DeadLetters.Snapshot().Should().ContainSingle().Subject;
        entry.Position.Should().Be(new GlobalPosition(2));
        entry.Attempts.Should().Be(4);
    }

    [Fact]
    public async Task RunTickAsync_PausedProjection_NotLedOnSubsequentTicks()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var applyCount = 0;
        var projection = new RecordingProjection<Incremented>
        {
            OnApply = (_, _) =>
            {
                applyCount++;
                throw new InvalidOperationException("poison");
            },
        };
        var pause = new ProjectionErrorPolicy(ErrorAction.Pause, MaxRetries: 0);
        var registration = kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection, pause);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [registration]);

        await daemon.RunTickAsync(Ct);
        await daemon.RunTickAsync(Ct);

        applyCount.Should().Be(1); // paused after the first failure (no retries) — not attempted again
        registration.IsHalted.Should().BeTrue();
    }

    [Fact]
    public async Task RunTickAsync_OnePoisonEvent_NeverStopsDaemon_HealthyProjectionContinues()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented(), new Decremented());
        var faulting = new RecordingProjection<Incremented>
        {
            OnApply = (_, _) => throw new InvalidOperationException("poison"),
        };
        var healthy = new RecordingProjection<Decremented>();
        var daemon = kit.Daemon(
            new ProjectionDaemonOptions(),
            [
                kit.Registration("faulting", AsyncProjectionTestKit.IncrementedOnly, faulting),
                kit.Registration("healthy", AsyncProjectionTestKit.DecrementedOnly, healthy),
            ]);

        await Awaiting(() => daemon.RunTickAsync(Ct).AsTask()).Should().NotThrowAsync();

        faulting.Applied.Should().BeEmpty();      // poison dead-lettered and skipped
        healthy.Applied.Should().ContainSingle(); // sibling projection unaffected
        kit.DeadLetters.Snapshot().Should().ContainSingle(e => e.ProjectionName == "faulting");
    }

    [Fact]
    public async Task RunTickAsync_DeadLetter_RecordsExceptionTypeAndMessageOnly_AndDoesNotInlineItInTheLog()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var logProvider = new ListLoggerProvider();
        var logger = new Logger<ProjectionDaemon>(new SingleProviderLoggerFactory(logProvider));
        const string exceptionText = "SECRET-DO-NOT-INLINE";
        var projection = new RecordingProjection<Incremented>
        {
            OnApply = (_, _) => throw new InvalidOperationException(exceptionText),
        };
        var daemon = kit.Daemon(
            new ProjectionDaemonOptions(),
            [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection)],
            logger: logger);

        await daemon.RunTickAsync(Ct);

        // The dead-letter Error is exactly type + ": " + host message — the library adds no payload/metadata.
        var entry = kit.DeadLetters.Snapshot().Should().ContainSingle().Subject;
        entry.Error.Should().Be($"{typeof(InvalidOperationException).FullName}: {exceptionText}");

        // An Error-level line was logged, but the rendered template never inlines the exception message text.
        logProvider.Entries.Should().Contain(e => e.Level == LogLevel.Error);
        logProvider.Entries.Should().NotContain(e => e.Message.Contains(exceptionText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTickAsync_DeadLetterAndSkip_AttemptsEqualInitialPlusMaxRetries()
    {
        var kit = new AsyncProjectionTestKit();
        await kit.AppendAsync(Ct, new Incremented());
        var applyCount = 0;
        var projection = new RecordingProjection<Incremented>
        {
            OnApply = (_, _) =>
            {
                applyCount++;
                throw new InvalidOperationException("poison");
            },
        };
        var policy = new ProjectionErrorPolicy(ErrorAction.DeadLetterAndSkip, MaxRetries: 2);
        var daemon = kit.Daemon(new ProjectionDaemonOptions(), [kit.Registration("counter", AsyncProjectionTestKit.IncrementedOnly, projection, policy)]);

        await daemon.RunTickAsync(Ct);

        applyCount.Should().Be(3); // initial + 2 retries
        kit.DeadLetters.Snapshot().Should().ContainSingle().Which.Attempts.Should().Be(3);
    }
}
