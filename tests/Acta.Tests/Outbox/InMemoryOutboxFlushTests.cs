using Xunit;

using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Outbox;

public sealed class InMemoryOutboxFlushTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    /// <summary>A minimal, non-in-memory <see cref="IEventAppendTransaction"/> used only to prove the type guard.</summary>
    private sealed class ForeignEventAppendTransaction : IEventAppendTransaction
    {
        public ValueTask<AppendResult> AppendAsync(
            string streamId, long expectedVersion, IReadOnlyList<EventData> events, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask CommitAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task FlushAsync_DrainsCollectorAndEnlistsIntoTransaction()
    {
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        var factory = new InMemoryEventAppendTransactionFactory();
        collector.Collect("first", CreateMetadata());
        collector.Collect("second", CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await flush.FlushAsync(tx, Ct);
            await tx.CommitAsync(Ct);
        }

        factory.OutboxState.CommittedOutbox.Should().HaveCount(2);
        factory.OutboxState.CommittedOutbox[0].Event.Should().Be("first");
        factory.OutboxState.CommittedOutbox[1].Event.Should().Be("second");
    }

    [Fact]
    public async Task FlushAsync_DrainsCollector_SubsequentDrainIsEmpty()
    {
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        var factory = new InMemoryEventAppendTransactionFactory();
        collector.Collect("only", CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await flush.FlushAsync(tx, Ct);
            await tx.CommitAsync(Ct);
        }

        collector.Drain().Should().BeEmpty();
    }

    [Fact]
    public async Task FlushAsync_WithForeignTransaction_ThrowsInvalidOperationException()
    {
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        var foreignTx = new ForeignEventAppendTransaction();

        var ex = (await Awaiting(
            () => flush.FlushAsync(foreignTx, Ct).AsTask()).Should().ThrowAsync<InvalidOperationException>()).Which;

        ex.Message.Should().Contain(nameof(ForeignEventAppendTransaction));
    }
}
