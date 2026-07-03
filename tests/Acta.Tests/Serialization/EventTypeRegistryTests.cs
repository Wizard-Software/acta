using System.Text.Json;

using Xunit;

using Acta.Serialization;

namespace Acta.Tests.Serialization;

public sealed class EventTypeRegistryTests
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Default;

    private sealed record OrderPlaced(string OrderId, string CustomerId);

    private sealed record OrderShipped(string OrderId, int Parcels);

    [Fact]
    public void Register_ReturnsSameRegistry_ForFluentChaining()
    {
        var registry = new EventTypeRegistry();

        var returned = registry.Register<OrderPlaced>("OrderPlaced");

        Assert.Same(registry, returned);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Register_DuplicateEventType_ThrowsArgumentException()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("Order");

        Assert.Throws<ArgumentException>(() => registry.Register<OrderShipped>("Order"));
    }

    [Fact]
    public void Register_DuplicateClrType_ThrowsArgumentException()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");

        Assert.Throws<ArgumentException>(() => registry.Register<OrderPlaced>("OrderPlacedAgain"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_NullOrWhitespaceEventType_ThrowsArgumentException(string? eventType)
    {
        var registry = new EventTypeRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register<OrderPlaced>(eventType!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Register_SchemaVersionBelowOne_ThrowsArgumentOutOfRangeException(int schemaVersion)
    {
        var registry = new EventTypeRegistry();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => registry.Register<OrderPlaced>("OrderPlaced", schemaVersion));
    }

    [Fact]
    public void Register_WithoutExplicitName_UsesClrTypeName()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>();

        var (eventType, schemaVersion) = registry.ResolveEventType(typeof(OrderPlaced));

        Assert.Equal(nameof(OrderPlaced), eventType);
        Assert.Equal(1, schemaVersion);
    }

    [Fact]
    public void ResolveClrType_RegisteredEventType_ReturnsClrType()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");

        Assert.Equal(typeof(OrderPlaced), registry.ResolveClrType("OrderPlaced"));
    }

    [Fact]
    public void ResolveClrType_UnknownEventType_ThrowsUnknownEventTypeException()
    {
        var registry = new EventTypeRegistry();

        var ex = Assert.Throws<UnknownEventTypeException>(() => registry.ResolveClrType("Ghost"));
        Assert.Equal("Ghost", ex.EventType);
    }

    [Fact]
    public void ResolveEventType_RegisteredClrType_ReturnsNameAndSchemaVersion()
    {
        var registry = new EventTypeRegistry().Register<OrderShipped>("OrderShipped", 3);

        var (eventType, schemaVersion) = registry.ResolveEventType(typeof(OrderShipped));

        Assert.Equal("OrderShipped", eventType);
        Assert.Equal(3, schemaVersion);
    }

    [Fact]
    public void ResolveEventType_UnregisteredClrType_ThrowsUnknownEventTypeException()
    {
        var registry = new EventTypeRegistry();

        Assert.Throws<UnknownEventTypeException>(() => registry.ResolveEventType(typeof(OrderPlaced)));
    }

    [Fact]
    public void TryResolveClrType_UnknownEventType_ReturnsFalse()
    {
        var registry = new EventTypeRegistry();

        Assert.False(registry.TryResolveClrType("Ghost", out var clrType));
        Assert.Null(clrType);
    }

    [Fact]
    public void Deserialize_RegisteredType_ReturnsTypedInstanceWithPayload()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new OrderPlaced("o-1", "c-1"), Options);

        var result = registry.Deserialize("OrderPlaced", payload, Options);

        var order = Assert.IsType<OrderPlaced>(result);
        Assert.Equal("o-1", order.OrderId);
        Assert.Equal("c-1", order.CustomerId);
    }

    [Fact]
    public void Deserialize_UnknownEventType_ThrowsUnknownEventTypeException()
    {
        var registry = new EventTypeRegistry();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new OrderPlaced("o-1", "c-1"), Options);

        Assert.Throws<UnknownEventTypeException>(() => registry.Deserialize("OrderPlaced", payload, Options));
    }

    [Fact]
    public void SerializePayload_ThenDeserialize_RoundTripsEvent()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");
        var original = new OrderPlaced("o-42", "c-7");

        var payload = registry.SerializePayload(original, Options);
        var roundTripped = registry.Deserialize("OrderPlaced", payload, Options);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void SerializePayload_UnregisteredClrType_ThrowsUnknownEventTypeException()
    {
        var registry = new EventTypeRegistry();

        Assert.Throws<UnknownEventTypeException>(
            () => registry.SerializePayload(new OrderPlaced("o-1", "c-1"), Options));
    }

    [Fact]
    public void UnknownEventTypeException_Message_NamesTheEventType()
    {
        var ex = new UnknownEventTypeException("OrderPlaced");

        Assert.Contains("OrderPlaced", ex.Message);
        Assert.Equal("OrderPlaced", ex.EventType);
    }
}
