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

        Assert.Equal(eventId, eventData.EventId);
        Assert.Equal("OrderPlaced", eventData.EventType);
        Assert.Equal(1, eventData.SchemaVersion);
        Assert.Equal(payload, eventData.Payload.ToArray());
        Assert.Same(metadata, eventData.Metadata);
    }

    [Fact]
    public void EventData_Equality_SamePayloadBuffer_AreEqual()
    {
        var eventId = Guid.CreateVersion7();
        var metadata = CreateMetadata();
        ReadOnlyMemory<byte> payload = new byte[] { 1, 2, 3 };

        var first = new EventData(eventId, "OrderPlaced", 1, payload, metadata);
        var second = new EventData(eventId, "OrderPlaced", 1, payload, metadata);

        Assert.Equal(first, second);
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

        Assert.Equal(eventId, storedEvent.EventId);
        Assert.Equal("stream-1", storedEvent.StreamId);
        Assert.Equal(3, storedEvent.Version);
        Assert.Equal(globalPosition, storedEvent.GlobalPosition);
        Assert.Equal("OrderPlaced", storedEvent.EventType);
        Assert.Equal(1, storedEvent.SchemaVersion);
        Assert.Equal(payload, storedEvent.Payload.ToArray());
        Assert.Same(metadata, storedEvent.Metadata);
        Assert.Equal(timestamp, storedEvent.Timestamp);
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

        Assert.Equal(first, second);
    }

    [Fact]
    public void SourcedEvent_Constructor_AssignsMembers()
    {
        var raw = new StoredEvent(
            Guid.CreateVersion7(), "stream-1", 0, GlobalPosition.Start, "OrderPlaced", 1,
            ReadOnlyMemory<byte>.Empty, CreateMetadata(), DateTimeOffset.UtcNow);
        object clrEvent = new { OrderId = Guid.CreateVersion7() };

        var sourcedEvent = new SourcedEvent(clrEvent, raw);

        Assert.Same(clrEvent, sourcedEvent.Event);
        Assert.Same(raw, sourcedEvent.Raw);
    }

    [Fact]
    public void Direction_HasForwardsAndBackwards()
    {
        Assert.True(Enum.IsDefined(Direction.Forwards));
        Assert.True(Enum.IsDefined(Direction.Backwards));
        Assert.NotEqual(Direction.Forwards, Direction.Backwards);
    }
}
