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

        returned.Should().BeSameAs(registry);
        registry.Count.Should().Be(1);
    }

    [Fact]
    public void Register_DuplicateEventType_ThrowsArgumentException()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("Order");

        Invoking(() => registry.Register<OrderShipped>("Order")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_DuplicateClrType_ThrowsArgumentException()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");

        Invoking(() => registry.Register<OrderPlaced>("OrderPlacedAgain")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_NullOrWhitespaceEventType_ThrowsArgumentException(string? eventType)
    {
        var registry = new EventTypeRegistry();

        Invoking(() => registry.Register<OrderPlaced>(eventType!)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Register_SchemaVersionBelowOne_ThrowsArgumentOutOfRangeException(int schemaVersion)
    {
        var registry = new EventTypeRegistry();

        Invoking(() => registry.Register<OrderPlaced>("OrderPlaced", schemaVersion))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Register_WithoutExplicitName_UsesClrTypeName()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>();

        var (eventType, schemaVersion) = registry.ResolveEventType(typeof(OrderPlaced));

        eventType.Should().Be(nameof(OrderPlaced));
        schemaVersion.Should().Be(1);
    }

    [Fact]
    public void ResolveClrType_RegisteredEventType_ReturnsClrType()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");

        registry.ResolveClrType("OrderPlaced").Should().Be(typeof(OrderPlaced));
    }

    [Fact]
    public void ResolveClrType_UnknownEventType_ThrowsUnknownEventTypeException()
    {
        var registry = new EventTypeRegistry();

        var ex = Invoking(() => registry.ResolveClrType("Ghost")).Should().Throw<UnknownEventTypeException>().Which;
        ex.EventType.Should().Be("Ghost");
    }

    [Fact]
    public void ResolveEventType_RegisteredClrType_ReturnsNameAndSchemaVersion()
    {
        var registry = new EventTypeRegistry().Register<OrderShipped>("OrderShipped", 3);

        var (eventType, schemaVersion) = registry.ResolveEventType(typeof(OrderShipped));

        eventType.Should().Be("OrderShipped");
        schemaVersion.Should().Be(3);
    }

    [Fact]
    public void ResolveEventType_UnregisteredClrType_ThrowsUnknownEventTypeException()
    {
        var registry = new EventTypeRegistry();

        Invoking(() => registry.ResolveEventType(typeof(OrderPlaced))).Should().Throw<UnknownEventTypeException>();
    }

    [Fact]
    public void TryResolveClrType_UnknownEventType_ReturnsFalse()
    {
        var registry = new EventTypeRegistry();

        registry.TryResolveClrType("Ghost", out var clrType).Should().BeFalse();
        clrType.Should().BeNull();
    }

    [Fact]
    public void Deserialize_RegisteredType_ReturnsTypedInstanceWithPayload()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new OrderPlaced("o-1", "c-1"), Options);

        var result = registry.Deserialize("OrderPlaced", payload, Options);

        var order = result.Should().BeOfType<OrderPlaced>().Subject;
        order.OrderId.Should().Be("o-1");
        order.CustomerId.Should().Be("c-1");
    }

    [Fact]
    public void Deserialize_UnknownEventType_ThrowsUnknownEventTypeException()
    {
        var registry = new EventTypeRegistry();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new OrderPlaced("o-1", "c-1"), Options);

        Invoking(() => registry.Deserialize("OrderPlaced", payload, Options)).Should().Throw<UnknownEventTypeException>();
    }

    [Fact]
    public void SerializePayload_ThenDeserialize_RoundTripsEvent()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");
        var original = new OrderPlaced("o-42", "c-7");

        var payload = registry.SerializePayload(original, Options);
        var roundTripped = registry.Deserialize("OrderPlaced", payload, Options);

        roundTripped.Should().Be(original);
    }

    [Fact]
    public void SerializePayload_UnregisteredClrType_ThrowsUnknownEventTypeException()
    {
        var registry = new EventTypeRegistry();

        Invoking(() => registry.SerializePayload(new OrderPlaced("o-1", "c-1"), Options))
            .Should().Throw<UnknownEventTypeException>();
    }

    [Fact]
    public void UnknownEventTypeException_Message_NamesTheEventType()
    {
        var ex = new UnknownEventTypeException("OrderPlaced");

        ex.Message.Should().Contain("OrderPlaced");
        ex.EventType.Should().Be("OrderPlaced");
    }

    [Fact]
    public void Register_BlankEventType_MessageAndParamNameDescribeTheFailure()
    {
        var registry = new EventTypeRegistry();

        var ex = Invoking(() => registry.Register<OrderPlaced>("   ")).Should().Throw<ArgumentException>().Which;
        ex.ParamName.Should().Be("eventType");
        ex.Message.Should().Contain("null or blank");
    }

    [Fact]
    public void Register_SchemaVersionBelowOne_MessageAndParamNameDescribeTheFailure()
    {
        var registry = new EventTypeRegistry();

        var ex = Invoking(() => registry.Register<OrderPlaced>("OrderPlaced", 0))
            .Should().Throw<ArgumentOutOfRangeException>().Which;
        ex.ParamName.Should().Be("schemaVersion");
        ex.Message.Should().Contain("Schema version");
    }

    [Fact]
    public void Register_DuplicateEventType_MessageNamesTheEventType()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("Order");

        var ex = Invoking(() => registry.Register<OrderShipped>("Order")).Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("Order");
        ex.Message.Should().Contain("already registered");
    }

    [Fact]
    public void Register_DuplicateClrType_MessageNamesTheClrTypeAndExistingEventType()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");

        var ex = Invoking(() => registry.Register<OrderPlaced>("OrderPlacedAgain")).Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("already registered as event type");
        ex.Message.Should().Contain("OrderPlaced");
    }

    [Fact]
    public void TryResolveClrType_RegisteredEventType_ReturnsTrueAndClrType()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");

        registry.TryResolveClrType("OrderPlaced", out var clrType).Should().BeTrue();
        clrType.Should().Be(typeof(OrderPlaced));
    }

    [Fact]
    public void TryResolveEventType_RegisteredClrType_ReturnsTrueWithNameAndSchemaVersion()
    {
        var registry = new EventTypeRegistry().Register<OrderShipped>("OrderShipped", 4);

        registry.TryResolveEventType(typeof(OrderShipped), out var eventType, out var schemaVersion).Should().BeTrue();
        eventType.Should().Be("OrderShipped");
        schemaVersion.Should().Be(4);
    }

    [Fact]
    public void TryResolveEventType_UnregisteredClrType_ReturnsFalseWithDefaults()
    {
        var registry = new EventTypeRegistry();

        registry.TryResolveEventType(typeof(OrderPlaced), out var eventType, out var schemaVersion).Should().BeFalse();
        eventType.Should().BeNull();
        schemaVersion.Should().Be(0);
    }

    [Fact]
    public void SerializePayload_NullEvent_ThrowsArgumentNullException()
    {
        var registry = new EventTypeRegistry().Register<OrderPlaced>("OrderPlaced");

        Invoking(() => registry.SerializePayload(null!, Options)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UnknownEventTypeException_ForClrType_MessageAndEventTypeUseFullName()
    {
        var ex = new UnknownEventTypeException(typeof(OrderPlaced));

        ex.Message.Should().Contain("not registered");
        ex.Message.Should().Contain(typeof(OrderPlaced).ToString());
        ex.EventType.Should().Be(typeof(OrderPlaced).FullName);
    }
}
