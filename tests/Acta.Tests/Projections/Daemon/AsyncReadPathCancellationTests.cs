using System.Runtime.CompilerServices;

using Xunit;

using Acta.Abstractions;
using Acta.Configuration;
using Acta.InMemory;
using Acta.Projections.Daemon;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Pins cooperative cancellation on the async projection read path (task 5.2-5.3): each entry point
/// observes an already-cancelled token BEFORE touching the store, so a stopping host tears the daemon
/// down without a wasted read/load. The spy store (which itself ignores the token and yields nothing)
/// makes the early check observable: with the guard the call throws; without it the call would reach
/// the store and return empty.
/// </summary>
public sealed class AsyncReadPathCancellationTests
{
    private static readonly CancellationToken Canceled = new(canceled: true);

    [Fact]
    public async Task HwmPoller_ReadSafeHighWaterMark_CanceledToken_ThrowsBeforeReadingStore()
    {
        var store = new NonCancellingSpyStore();
        var poller = new HwmPoller(store, new ProjectionDaemonOptions());

        await Awaiting(() => poller.ReadSafeHighWaterMarkAsync(Canceled).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();

        store.ReadAllInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task InMemoryCheckpointSink_Load_CanceledToken_Throws()
    {
        var sink = new InMemoryCheckpointSink();

        await Awaiting(() => sink.LoadAsync("counter", tenantId: null, Canceled).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task InMemorySubscriptionSource_ReadBatch_CanceledToken_ThrowsBeforeReadingStore()
    {
        var store = new NonCancellingSpyStore();
        var source = new InMemorySubscriptionSource(store);

        await Awaiting(() => source.ReadBatchAsync(GlobalPosition.Start, maxCount: 1, eventTypes: null, ct: Canceled).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();

        store.ReadAllInvoked.Should().BeFalse();
    }

    /// <summary>
    /// An <see cref="IEventStore"/> spy whose <see cref="ReadAllAsync"/> records that it ran and yields
    /// nothing WITHOUT observing the cancellation token — so if a read-path guard is removed, the call
    /// reaches the store and returns empty instead of throwing, and the test fails (killing the mutant).
    /// </summary>
    private sealed class NonCancellingSpyStore : IEventStore
    {
        public bool ReadAllInvoked { get; private set; }

        public async IAsyncEnumerable<StoredEvent> ReadAllAsync(
            GlobalPosition from,
            GlobalPosition? upTo = null,
            int? maxCount = null,
            Direction direction = Direction.Forwards,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ReadAllInvoked = true;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<AppendResult> AppendAsync(string streamId, long expectedVersion, IReadOnlyList<EventData> events, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<StoredEvent> ReadStreamAsync(string streamId, long fromVersion = 0, long? toVersion = null, Direction direction = Direction.Forwards, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
