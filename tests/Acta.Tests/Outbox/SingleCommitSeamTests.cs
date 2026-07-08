using Xunit;

using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Outbox;

/// <summary>
/// AK-1 (ADR-002, FR-14): domain append and outbox enlistment through the same
/// <see cref="IEventAppendTransaction"/> become visible atomically — one all-or-nothing commit,
/// or nothing at all on rollback (dispose without commit).
/// </summary>
public sealed class SingleCommitSeamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    private static EventData CreateEventData(string eventType = "TestEvent") =>
        new(Guid.NewGuid(), eventType, 1, new byte[] { 1, 2, 3 }, CreateMetadata());

    private static EventData[] CreateBatch(int count) => [.. Enumerable.Range(0, count).Select(_ => CreateEventData())];

    /// <summary>A <see cref="TimeProvider"/> that always reports a fixed, caller-supplied instant.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Commit_MakesAppendAndOutboxVisibleAtomically()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        collector.Collect("OrderPlacedIntegrationEvent", CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
            await flush.FlushAsync(tx, Ct);
            await tx.CommitAsync(Ct);
        }

        factory.OutboxState.ReadStream("order-1").Should().HaveCount(1);
        factory.OutboxState.CommittedOutbox.Should().HaveCount(1);
    }

    [Fact]
    public async Task Dispose_WithoutCommit_LeavesNothingVisible()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        collector.Collect("OrderPlacedIntegrationEvent", CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
            await flush.FlushAsync(tx, Ct);
            // No CommitAsync call — DisposeAsync at the end of this block must roll back.
        }

        factory.OutboxState.ReadStream("order-1").Should().BeEmpty();
        factory.OutboxState.CommittedOutbox.Should().BeEmpty();
    }

    [Fact]
    public async Task Commit_KeepsDomainAndIntegrationEventsSeparate()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        collector.Collect("OrderPlacedIntegrationEvent", CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
            await flush.FlushAsync(tx, Ct);
            await tx.CommitAsync(Ct);
        }

        var streamEvents = factory.OutboxState.ReadStream("order-1");
        streamEvents.Should().OnlyContain(e => e.EventType == "TestEvent");
        factory.OutboxState.CommittedOutbox.Should().OnlyContain(e => e.Event.Equals("OrderPlacedIntegrationEvent"));
        factory.OutboxState.CommittedEventCount.Should().Be(2);
    }

    [Fact]
    public async Task Commit_WithExplicitTimeProvider_StampsStoredEventTimestamp()
    {
        var fixedTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var factory = new InMemoryEventAppendTransactionFactory(new FixedTimeProvider(fixedTime));

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
            await tx.CommitAsync(Ct);
        }

        factory.OutboxState.ReadStream("order-1")[0].Timestamp.Should().Be(fixedTime);
    }

    [Fact]
    public async Task AppendAsync_NewStream_ReturnsCorrectNextExpectedVersionAndGlobalPosition()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        var result = await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        result.NextExpectedVersion.Should().Be(2);
        result.LastGlobalPosition.Value.Should().Be(3);
        result.Deduplicated.Should().BeFalse();
    }

    [Fact]
    public async Task AppendAsync_ExpectedVersionMismatch_ThrowsConcurrencyException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);

        var ex = (await Awaiting(
            () => tx.AppendAsync("order-1", 5, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<ConcurrencyException>()).Which;

        ex.StreamId.Should().Be("order-1");
        ex.ExpectedVersion.Should().Be(5);
        ex.ActualVersion.Should().Be(0);
    }

    [Fact]
    public async Task CommitAsync_CalledTwice_ThrowsInvalidOperationException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        await tx.CommitAsync(Ct);

        await Awaiting(() => tx.CommitAsync(Ct).AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppendAsync_AfterCommit_ThrowsInvalidOperationException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        await tx.CommitAsync(Ct);

        await Awaiting(
            () => tx.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppendAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var tx = await factory.BeginAsync(Ct);
        await tx.DisposeAsync();

        await Awaiting(
            () => tx.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task CommitAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var tx = await factory.BeginAsync(Ct);
        await tx.DisposeAsync();

        await Awaiting(() => tx.CommitAsync(Ct).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
    }
}
