using Xunit;

using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Outbox;

public sealed class InMemoryIntegrationEventCollectorTests
{
    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    [Fact]
    public void Drain_NothingCollected_ReturnsEmpty()
    {
        var collector = new InMemoryIntegrationEventCollector();

        var drained = collector.Drain();

        drained.Should().BeEmpty();
    }

    [Fact]
    public void Collect_ThenDrain_ReturnsEventsInCollectionOrder()
    {
        var collector = new InMemoryIntegrationEventCollector();
        var first = ("first", CreateMetadata());
        var second = ("second", CreateMetadata());

        collector.Collect(first.Item1, first.Item2);
        collector.Collect(second.Item1, second.Item2);
        var drained = collector.Drain();

        drained.Should().HaveCount(2);
        drained[0].Should().Be(new CollectedIntegrationEvent(first.Item1, first.Item2));
        drained[1].Should().Be(new CollectedIntegrationEvent(second.Item1, second.Item2));
    }

    [Fact]
    public void Drain_CalledTwice_SecondCallReturnsEmptyBufferWasCleared()
    {
        var collector = new InMemoryIntegrationEventCollector();
        collector.Collect("only", CreateMetadata());
        collector.Drain();

        var secondDrain = collector.Drain();

        secondDrain.Should().BeEmpty();
    }
}
