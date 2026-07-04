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
        Invoking(() => new EventSerializer(null!, Options)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Invoking(() => new EventSerializer(CreateRegistry(), null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToEventData_RegisteredEvent_StampsEventTypeAndSchemaVersion()
    {
        var registry = new EventTypeRegistry().Register<OrderShipped>("OrderShipped", 3);
        var serializer = new EventSerializer(registry, Options);
        var metadata = CreateMetadata();
        var eventId = Guid.NewGuid();

        var eventData = serializer.ToEventData(new OrderShipped("o-1", 2), metadata, eventId);

        eventData.EventType.Should().Be("OrderShipped");
        eventData.SchemaVersion.Should().Be(3);
        eventData.EventId.Should().Be(eventId);
        eventData.Metadata.Should().BeSameAs(metadata);
    }

    [Fact]
    public void ToEventData_SerializesPayloadWithoutMetadata()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata();

        var eventData = serializer.ToEventData(new OrderPlaced("o-1", "c-1"), metadata, Guid.NewGuid());

        var roundTripped = JsonSerializer.Deserialize<OrderPlaced>(eventData.Payload.Span, Options);
        roundTripped.Should().NotBeNull();
        roundTripped!.OrderId.Should().Be("o-1");
        roundTripped.CustomerId.Should().Be("c-1");

        var payloadText = Encoding.UTF8.GetString(eventData.Payload.Span);
        payloadText.Should().NotContain("CorrelationId");
        payloadText.Should().NotContain("MessageId");
    }

    [Fact]
    public void ToEventData_UnregisteredEvent_ThrowsUnknownEventTypeException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Invoking(() => serializer.ToEventData(new OrderShipped("o-1", 1), CreateMetadata(), Guid.NewGuid()))
            .Should().Throw<UnknownEventTypeException>();
    }

    [Fact]
    public void ToEventData_NullEvent_ThrowsArgumentNullException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Invoking(() => serializer.ToEventData(null!, CreateMetadata(), Guid.NewGuid()))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToEventData_NullMetadata_ThrowsArgumentNullException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Invoking(() => serializer.ToEventData(new OrderPlaced("o-1", "c-1"), null!, Guid.NewGuid()))
            .Should().Throw<ArgumentNullException>();
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

        var order = sourced.Event.Should().BeOfType<OrderPlaced>().Subject;
        order.OrderId.Should().Be("o-9");
        order.CustomerId.Should().Be("c-9");
        sourced.Raw.Should().BeSameAs(stored);
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

        Invoking(() => serializer.ToSourcedEvent(stored)).Should().Throw<UnknownEventTypeException>();
    }

    [Fact]
    public void ToSourcedEvent_NullStored_ThrowsArgumentNullException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Invoking(() => serializer.ToSourcedEvent(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SerializeMetadata_ThenDeserialize_RoundTripsAllFields()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata();

        var bytes = serializer.SerializeMetadata(metadata);
        var roundTripped = serializer.DeserializeMetadata(bytes);

        roundTripped.MessageId.Should().Be(metadata.MessageId);
        roundTripped.CorrelationId.Should().Be(metadata.CorrelationId);
        roundTripped.CausationId.Should().Be(metadata.CausationId);
        roundTripped.Timestamp.Should().Be(metadata.Timestamp);
        roundTripped.TenantId.Should().Be(metadata.TenantId);
        roundTripped.TraceParent.Should().Be(metadata.TraceParent);
        roundTripped.TraceState.Should().Be(metadata.TraceState);
    }

    [Fact]
    public void SerializeMetadata_WithUserRef_RoundTripsPseudonymAsString()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata(new UserRef("acct-123"));

        var bytes = serializer.SerializeMetadata(metadata);
        var roundTripped = serializer.DeserializeMetadata(bytes);

        roundTripped.User!.Value.Value.Should().Be("acct-123");

        var json = Encoding.UTF8.GetString(bytes);
        json.Should().Contain("\"acct-123\"");
        json.Should().NotContain("\"Value\":");
    }

    [Fact]
    public void SerializeMetadata_NullUser_RoundTripsAsNull()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata(user: null);

        var bytes = serializer.SerializeMetadata(metadata);
        var roundTripped = serializer.DeserializeMetadata(bytes);

        roundTripped.User.Should().BeNull();
    }

    [Fact]
    public void SerializeMetadata_WithExtensions_RoundTripsEntries()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var metadata = CreateMetadata() with { Extensions = new Dictionary<string, string> { ["k"] = "v" } };

        var bytes = serializer.SerializeMetadata(metadata);
        var roundTripped = serializer.DeserializeMetadata(bytes);

        roundTripped.Extensions.Should().NotBeNull();
        roundTripped.Extensions!["k"].Should().Be("v");
    }

    [Fact]
    public void DeserializeMetadata_UserRefContainingAtSign_Throws()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var baseline = Encoding.UTF8.GetString(serializer.SerializeMetadata(CreateMetadata(user: null)));
        var mutated = baseline.Replace("\"User\":null", "\"User\":\"bad@x\"");
        mutated.Should().NotBe(baseline);
        var bytes = Encoding.UTF8.GetBytes(mutated);

        // Empirically observed: System.Text.Json does NOT wrap an arbitrary exception thrown by a
        // custom converter's Read() in JsonException — the UserRef constructor's
        // ArgumentException propagates unchanged.
        Invoking(() => serializer.DeserializeMetadata(bytes)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeserializeMetadata_UserNonStringToken_ThrowsJsonException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var baseline = Encoding.UTF8.GetString(serializer.SerializeMetadata(CreateMetadata(user: null)));
        var mutated = baseline.Replace("\"User\":null", "\"User\":123");
        mutated.Should().NotBe(baseline);
        var bytes = Encoding.UTF8.GetBytes(mutated);

        Invoking(() => serializer.DeserializeMetadata(bytes)).Should().Throw<JsonException>();
    }

    [Fact]
    public void DeserializeMetadata_NullJson_ThrowsJsonException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Invoking(() => serializer.DeserializeMetadata("null"u8.ToArray())).Should().Throw<JsonException>();
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

        sourced.Event.Should().BeOfType<OrderPlaced>().Subject.Should().Be(original);
    }

    [Fact]
    public void SerializeMetadata_NullMetadata_ThrowsArgumentNullException()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Invoking(() => serializer.SerializeMetadata(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeserializeMetadata_NullJson_MessageDescribesNullResult()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);

        Invoking(() => serializer.DeserializeMetadata("null"u8.ToArray()))
            .Should().Throw<JsonException>().WithMessage("*deserialized to null*");
    }

    [Fact]
    public void DeserializeMetadata_UserNonStringToken_MessageNamesUserRef()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var baseline = Encoding.UTF8.GetString(serializer.SerializeMetadata(CreateMetadata(user: null)));
        var mutated = baseline.Replace("\"User\":null", "\"User\":123");
        var bytes = Encoding.UTF8.GetBytes(mutated);

        Invoking(() => serializer.DeserializeMetadata(bytes))
            .Should().Throw<JsonException>().WithMessage("*UserRef*");
    }

    [Fact]
    public void DeserializeMetadata_UserNonStringToken_ThrowsWithConverterOwnMessage()
    {
        var serializer = new EventSerializer(CreateRegistry(), Options);
        var baseline = Encoding.UTF8.GetString(serializer.SerializeMetadata(CreateMetadata(user: null)));
        var mutated = baseline.Replace("\"User\":null", "\"User\":123");
        var bytes = Encoding.UTF8.GetBytes(mutated);

        // Exact (non-wildcard) match, deliberately: System.Text.Json substitutes a generic
        // "The JSON value could not be converted to ... Path: $.User | ..." message whenever a
        // converter-thrown JsonException carries a null/empty Message (its internal
        // AppendPathInformation flag flips on for empty messages). That generic fallback text
        // happens to *also* contain the substring "UserRef" (via "...Nullable`1[UserRef]..."), so
        // a "*UserRef*" wildcard (see the test above) passes for either the converter's own
        // message or the framework's unrelated fallback — it cannot tell them apart. Pinning the
        // exact, full text asserts the converter's own guard-clause message was actually produced,
        // and fails if either the throw is removed or its string literal is emptied.
        Invoking(() => serializer.DeserializeMetadata(bytes))
            .Should().Throw<JsonException>()
            .WithMessage("Expected a JSON string value for UserRef.");
    }
}
