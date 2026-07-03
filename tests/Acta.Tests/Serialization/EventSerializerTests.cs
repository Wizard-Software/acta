using System.Text;
using System.Text.Json;

using Xunit;

using Acta.Abstractions;
using Acta.Serialization;

namespace Acta.Tests.Serialization;

public sealed class EventSerializerTests
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Default;

    private sealed record OrderPlaced(string OrderId, string CustomerId);

    private sealed record OrderShipped(string OrderId, int Parcels);

    private static EventMetadata CreateMetadata(UserRef? user = null) => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
        Timestamp = new DateTimeOffset(2026, 7, 3, 10, 15, 30, 250, TimeSpan.FromHours(2)),
        User = user,
        TenantId = "tenant-1",
        TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        TraceState = "congo=t61rcWkgMzE",
    };

    private static EventTypeRegistry CreateRegistry()
        => new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");

    [Fact]
    public void Constructor_NullRegistry_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new EventSerializer(null!, Options));
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new EventSerializer(CreateRegistry(), null!));
    }

    [Fact]
    public void ToEventData_RegisteredEvent_StampsEventTypeAndSchemaVersion()
    {
        var registry = new EventTypeRegistry().Register<OrderShipped>("OrderShipped", 3);
        var serializer = new EventSerializer(registry, Options);
        var metadata = CreateMetadata();
        var eventId = Guid.NewGuid();

        var eventData = serializer.ToEventData(new OrderShipped("o-1", 2), metadata, eventId);

        Assert.Equal("OrderShipped", eventData.EventType);
        Assert.Equal(3, eventData.SchemaVersion);
        Assert.Equal(eventId, eventData.EventId);
        Assert.Same(metadata, eventData.Metadata);
    }

    [Fact]
    public void ToEventData_SerializesPayloadWithoutMetadata()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata();

        var eventData = serializer.ToEventData(new OrderPlaced("o-1", "c-1"), metadata, Guid.NewGuid());

        var roundTripped = JsonSerializer.Deserialize<OrderPlaced>(eventData.Payload.Span, Options);
        Assert.NotNull(roundTripped);
        Assert.Equal("o-1", roundTripped.OrderId);
        Assert.Equal("c-1", roundTripped.CustomerId);

        var payloadText = Encoding.UTF8.GetString(eventData.Payload.Span);
        Assert.DoesNotContain("CorrelationId", payloadText);
        Assert.DoesNotContain("MessageId", payloadText);
    }

    [Fact]
    public void ToEventData_UnregisteredEvent_ThrowsUnknownEventTypeException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Assert.Throws<UnknownEventTypeException>(
            () => serializer.ToEventData(new OrderShipped("o-1", 1), CreateMetadata(), Guid.NewGuid()));
    }

    [Fact]
    public void ToEventData_NullEvent_ThrowsArgumentNullException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Assert.Throws<ArgumentNullException>(
            () => serializer.ToEventData(null!, CreateMetadata(), Guid.NewGuid()));
    }

    [Fact]
    public void ToEventData_NullMetadata_ThrowsArgumentNullException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Assert.Throws<ArgumentNullException>(
            () => serializer.ToEventData(new OrderPlaced("o-1", "c-1"), null!, Guid.NewGuid()));
    }

    [Fact]
    public void ToSourcedEvent_RegisteredType_DeserializesPayloadIntoEvent()
    {
        var registry = CreateRegistry();
        var serializer = new EventSerializer(registry, Options);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new OrderPlaced("o-9", "c-9"), Options);
        var stored = new StoredEvent(
            Guid.NewGuid(),
            "stream-1",
            0,
            GlobalPosition.Start,
            "OrderPlaced",
            1,
            payload,
            CreateMetadata(),
            DateTimeOffset.UtcNow);

        var sourced = serializer.ToSourcedEvent(stored);

        var order = Assert.IsType<OrderPlaced>(sourced.Event);
        Assert.Equal("o-9", order.OrderId);
        Assert.Equal("c-9", order.CustomerId);
        Assert.Same(stored, sourced.Raw);
    }

    [Fact]
    public void ToSourcedEvent_UnknownStoredEventType_ThrowsUnknownEventTypeException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var stored = new StoredEvent(
            Guid.NewGuid(),
            "stream-1",
            0,
            GlobalPosition.Start,
            "Ghost",
            1,
            JsonSerializer.SerializeToUtf8Bytes(new OrderPlaced("o-1", "c-1"), Options),
            CreateMetadata(),
            DateTimeOffset.UtcNow);

        Assert.Throws<UnknownEventTypeException>(() => serializer.ToSourcedEvent(stored));
    }

    [Fact]
    public void ToSourcedEvent_NullStored_ThrowsArgumentNullException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Assert.Throws<ArgumentNullException>(() => serializer.ToSourcedEvent(null!));
    }

    [Fact]
    public void SerializeMetadata_ThenDeserialize_RoundTripsAllFields()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata();

        var bytes = serializer.SerializeMetadata(metadata);
        var roundTripped = serializer.DeserializeMetadata(bytes);

        Assert.Equal(metadata.MessageId, roundTripped.MessageId);
        Assert.Equal(metadata.CorrelationId, roundTripped.CorrelationId);
        Assert.Equal(metadata.CausationId, roundTripped.CausationId);
        Assert.Equal(metadata.Timestamp, roundTripped.Timestamp);
        Assert.Equal(metadata.TenantId, roundTripped.TenantId);
        Assert.Equal(metadata.TraceParent, roundTripped.TraceParent);
        Assert.Equal(metadata.TraceState, roundTripped.TraceState);
    }

    [Fact]
    public void SerializeMetadata_WithUserRef_RoundTripsPseudonymAsString()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata(new UserRef("acct-123"));

        var bytes = serializer.SerializeMetadata(metadata);
        var roundTripped = serializer.DeserializeMetadata(bytes);

        Assert.Equal("acct-123", roundTripped.User!.Value.Value);

        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"acct-123\"", json);
        Assert.DoesNotContain("\"Value\":", json);
    }

    [Fact]
    public void SerializeMetadata_NullUser_RoundTripsAsNull()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata(user: null);

        var bytes = serializer.SerializeMetadata(metadata);
        var roundTripped = serializer.DeserializeMetadata(bytes);

        Assert.Null(roundTripped.User);
    }

    [Fact]
    public void SerializeMetadata_WithExtensions_RoundTripsEntries()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata() with { Extensions = new Dictionary<string, string> { ["k"] = "v" } };

        var bytes = serializer.SerializeMetadata(metadata);
        var roundTripped = serializer.DeserializeMetadata(bytes);

        Assert.NotNull(roundTripped.Extensions);
        Assert.Equal("v", roundTripped.Extensions!["k"]);
    }

    [Fact]
    public void DeserializeMetadata_UserRefContainingAtSign_Throws()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var baseline = Encoding.UTF8.GetString(serializer.SerializeMetadata(CreateMetadata(user: null)));
        var mutated = baseline.Replace("\"User\":null", "\"User\":\"bad@x\"");
        Assert.NotEqual(baseline, mutated);
        var bytes = Encoding.UTF8.GetBytes(mutated);

        // Empirically observed: System.Text.Json does NOT wrap an arbitrary exception thrown by a
        // custom converter's Read() in JsonException — the UserRef constructor's
        // ArgumentException propagates unchanged.
        Assert.Throws<ArgumentException>(() => serializer.DeserializeMetadata(bytes));
    }

    [Fact]
    public void DeserializeMetadata_UserNonStringToken_ThrowsJsonException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var baseline = Encoding.UTF8.GetString(serializer.SerializeMetadata(CreateMetadata(user: null)));
        var mutated = baseline.Replace("\"User\":null", "\"User\":123");
        Assert.NotEqual(baseline, mutated);
        var bytes = Encoding.UTF8.GetBytes(mutated);

        Assert.Throws<JsonException>(() => serializer.DeserializeMetadata(bytes));
    }

    [Fact]
    public void DeserializeMetadata_NullJson_ThrowsJsonException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Assert.Throws<JsonException>(() => serializer.DeserializeMetadata("null"u8.ToArray()));
    }

    [Fact]
    public void ToEventData_ThenToSourcedEvent_RoundTripsEventThroughPayload()
    {
        var registry = CreateRegistry();
        var serializer = new EventSerializer(registry, Options);
        var original = new OrderPlaced("o-77", "c-77");

        var eventData = serializer.ToEventData(original, CreateMetadata(), Guid.NewGuid());
        var stored = new StoredEvent(
            eventData.EventId,
            "stream-1",
            0,
            GlobalPosition.Start,
            eventData.EventType,
            eventData.SchemaVersion,
            eventData.Payload,
            eventData.Metadata,
            DateTimeOffset.UtcNow);

        var sourced = serializer.ToSourcedEvent(stored);

        Assert.Equal(original, Assert.IsType<OrderPlaced>(sourced.Event));
    }

    [Fact]
    public void SerializeMetadata_NullMetadata_ThrowsArgumentNullException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Assert.Throws<ArgumentNullException>(() => serializer.SerializeMetadata(null!));
    }

    [Fact]
    public void DeserializeMetadata_NullJson_MessageDescribesNullResult()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        var ex = Assert.Throws<JsonException>(() => serializer.DeserializeMetadata("null"u8.ToArray()));
        Assert.Contains("deserialized to null", ex.Message);
    }

    [Fact]
    public void DeserializeMetadata_UserNonStringToken_MessageNamesUserRef()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var baseline = Encoding.UTF8.GetString(serializer.SerializeMetadata(CreateMetadata(user: null)));
        var mutated = baseline.Replace("\"User\":null", "\"User\":123");
        var bytes = Encoding.UTF8.GetBytes(mutated);

        var ex = Assert.Throws<JsonException>(() => serializer.DeserializeMetadata(bytes));
        Assert.Contains("UserRef", ex.Message);
    }
}
