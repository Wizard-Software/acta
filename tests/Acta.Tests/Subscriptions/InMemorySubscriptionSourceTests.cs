using Xunit;

using Acta.Abstractions;
using Acta.InMemory;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Subscriptions;

/// <summary>
/// Backend-specific unit tests for <see cref="InMemorySubscriptionSource"/> — argument guards and
/// cancellation, which are in-memory defaults rather than cross-backend contract facts.
/// </summary>
public sealed class InMemorySubscriptionSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Constructor_NullStore_ThrowsArgumentNullException()
    {
        Invoking(() => new InMemorySubscriptionSource(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadBatch_MaxCountZero_ThrowsArgumentOutOfRangeException()
    {
        var source = new InMemorySubscriptionSource(new InMemoryEventStore());

        await Awaiting(() => source.ReadBatchAsync(GlobalPosition.Start, 0, ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadBatch_NegativeMaxCount_ThrowsArgumentOutOfRangeException()
    {
        var source = new InMemorySubscriptionSource(new InMemoryEventStore());

        await Awaiting(() => source.ReadBatchAsync(GlobalPosition.Start, -1, ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadBatch_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);
        var source = new InMemorySubscriptionSource(store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => source.ReadBatchAsync(GlobalPosition.Start, 10, ct: cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReadFrom_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);
        var source = new InMemorySubscriptionSource(store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(async () =>
        {
            await foreach (var _ in source.ReadFromAsync(GlobalPosition.Start, cts.Token))
            {
            }
        }).Should().ThrowAsync<OperationCanceledException>();
    }
}
