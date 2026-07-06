using Xunit;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.InMemory;
using Acta.Projections.Daemon;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Unit tests for <see cref="HwmPoller"/> (task 5.2, step 3): the Tier-1 safe high-water-mark read —
/// empty store yields <see cref="GlobalPosition.Start"/>, a populated store yields the all-stream
/// head, the mark advances with appends, and an already-cancelled token throws. The
/// <see cref="ProjectionDaemonOptions.VisibilityLag"/> cutback is zero for the in-memory backend, so
/// the negative time-cutback case is deferred to the Postgres poller (Feature 7).
/// </summary>
public sealed class HwmPollerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HwmPoller CreatePoller(IEventStore store) => new(store, new ProjectionDaemonOptions());

    private static async ValueTask AppendAsync(IEventStore store, int count)
        => await store.AppendAsync("stream-1", ExpectedVersion.Any, TestEvents.Distinct(count), Ct);

    [Fact]
    public async Task ReadSafeHighWaterMarkAsync_EmptyStore_ReturnsStart()
    {
        var poller = CreatePoller(new InMemoryEventStore());

        var hwm = await poller.ReadSafeHighWaterMarkAsync(Ct);

        hwm.Should().Be(GlobalPosition.Start);
    }

    [Fact]
    public async Task ReadSafeHighWaterMarkAsync_AfterAppendingEvents_ReturnsHeadPosition()
    {
        var store = new InMemoryEventStore();
        await AppendAsync(store, 3);

        var hwm = await CreatePoller(store).ReadSafeHighWaterMarkAsync(Ct);

        // The first appended event gets GlobalPosition 1, so three events put the head at 3.
        hwm.Should().Be(new GlobalPosition(3));
    }

    [Fact]
    public async Task ReadSafeHighWaterMarkAsync_AfterFurtherAppends_AdvancesWithTheHead()
    {
        var store = new InMemoryEventStore();
        var poller = CreatePoller(store);
        await AppendAsync(store, 2);

        var first = await poller.ReadSafeHighWaterMarkAsync(Ct);
        await AppendAsync(store, 4);
        var second = await poller.ReadSafeHighWaterMarkAsync(Ct);

        first.Should().Be(new GlobalPosition(2));
        second.Should().Be(new GlobalPosition(6));
    }

    [Fact]
    public async Task ReadSafeHighWaterMarkAsync_AlreadyCancelledToken_Throws()
    {
        var store = new InMemoryEventStore();
        await AppendAsync(store, 1);
        var poller = CreatePoller(store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Awaiting(() => poller.ReadSafeHighWaterMarkAsync(cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
