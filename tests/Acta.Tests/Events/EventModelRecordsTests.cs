using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class EventModelRecordsTests
{
    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = Guid.CreateVersion7(),
        CausationId = Guid.CreateVersion7(),
    };

    [Fact]
    public void EventData_Constructor_AssignsMembers()
    {
        var eventId = Guid.CreateVersion7();
        var payload = new byte[] { 1, 2, 3 };
        var metadata = CreateMetadata();

        var eventData = new EventData(eventId, "OrderPlaced", 1, payload, metadata);

        eventData.EventId.Should().Be(eventId);
        eventData.EventType.Should().Be("OrderPlaced");
        eventData.SchemaVersion.Should().Be(1);
        eventData.Payload.ToArray().Should().Equal(payload);
        eventData.Metadata.Should().BeSameAs(metadata);
    }

    [Fact]
    public void EventData_Equality_SamePayloadBuffer_AreEqual()
    {
        var eventId = Guid.CreateVersion7();
        var metadata = CreateMetadata();
        ReadOnlyMemory<byte> payload = new byte[] { 1, 2, 3 };

        var first = new EventData(eventId, "OrderPlaced", 1, payload, metadata);
        var second = new EventData(eventId, "OrderPlaced", 1, payload, metadata);

        second.Should().Be(first);
    }

    [Fact]
    public void StoredEvent_Constructor_AssignsMembers()
    {
        var eventId = Guid.CreateVersion7();
        var globalPosition = new GlobalPosition(10);
        var payload = new byte[] { 4, 5, 6 };
        var metadata = CreateMetadata();
        var timestamp = DateTimeOffset.UtcNow;

        var storedEvent = new StoredEvent(
            eventId, "stream-1", 3, globalPosition, "OrderPlaced", 1, payload, metadata, timestamp);

        storedEvent.EventId.Should().Be(eventId);
        storedEvent.StreamId.Should().Be("stream-1");
        storedEvent.Version.Should().Be(3);
        storedEvent.GlobalPosition.Should().Be(globalPosition);
        storedEvent.EventType.Should().Be("OrderPlaced");
        storedEvent.SchemaVersion.Should().Be(1);
        storedEvent.Payload.ToArray().Should().Equal(payload);
        storedEvent.Metadata.Should().BeSameAs(metadata);
        storedEvent.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void StoredEvent_Equality_SamePayloadBuffer_AreEqual()
    {
        var eventId = Guid.CreateVersion7();
        var globalPosition = new GlobalPosition(10);
        var metadata = CreateMetadata();
        var timestamp = DateTimeOffset.UtcNow;
        ReadOnlyMemory<byte> payload = new byte[] { 4, 5, 6 };

        var first = new StoredEvent(
            eventId, "stream-1", 3, globalPosition, "OrderPlaced", 1, payload, metadata, timestamp);
        var second = new StoredEvent(
            eventId, "stream-1", 3, globalPosition, "OrderPlaced", 1, payload, metadata, timestamp);

        second.Should().Be(first);
    }

    [Fact]
    public void SourcedEvent_Constructor_AssignsMembers()
    {
        var raw = new StoredEvent(
            Guid.CreateVersion7(), "stream-1", 0, GlobalPosition.Start, "OrderPlaced", 1,
            ReadOnlyMemory<byte>.Empty, CreateMetadata(), DateTimeOffset.UtcNow);
        object clrEvent = new { OrderId = Guid.CreateVersion7() };

        var sourcedEvent = new SourcedEvent(clrEvent, raw);

        sourcedEvent.Event.Should().BeSameAs(clrEvent);
        sourcedEvent.Raw.Should().BeSameAs(raw);
    }

    [Fact]
    public void Direction_HasForwardsAndBackwards()
    {
        Enum.IsDefined(Direction.Forwards).Should().BeTrue();
        Enum.IsDefined(Direction.Backwards).Should().BeTrue();
        Direction.Backwards.Should().NotBe(Direction.Forwards);
    }
}
