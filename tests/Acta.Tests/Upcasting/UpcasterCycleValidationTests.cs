using Xunit;

using Acta.Abstractions;
using Acta.Tests.TestSupport;
using Acta.Upcasting;

namespace Acta.Tests.Upcasting;

public sealed class UpcasterCycleValidationTests
{
    private static ConfigurableEventUpcaster Linear(string eventType, int fromSchemaVersion, string toEventType, int toSchemaVersion) => new()
    {
        EventType = eventType,
        FromSchemaVersion = fromSchemaVersion,
        Transform = (e, _) => [new UpcastedEvent(e, toEventType, toSchemaVersion)],
    };

    [Fact]
    public void Constructor_DuplicateKey_DifferentInstances_ThrowsUpcasterCycleException()
    {
        var first = Linear("X", 1, "X", 2);
        var second = Linear("X", 1, "X", 3);

        var ex = Invoking(() => new UpcasterChain([first, second]))
            .Should().Throw<UpcasterCycleException>().Which;

        ex.EventType.Should().Be("X");
        ex.SchemaVersion.Should().Be(1);
        ex.VisitedPath.Should().BeEmpty();
        ex.Message.Should().Contain("Ambiguous");
    }

    [Fact]
    public void Constructor_SameInstanceRegisteredTwice_ThrowsUpcasterCycleException()
    {
        // The "statically-detectable same-type cycle" case (plan §2.4 / §9 Q1): the port does not
        // declare a target, so the only registration-time signal available without invoking any
        // upcaster is the identity of the registered instances themselves. Registering the exact
        // same instance twice for the same (EventType, FromSchemaVersion) key is an unconditional,
        // detectable self-reference — distinguished from a generic ambiguous duplicate (different
        // instances sharing a key) by a dedicated message.
        var upcaster = Linear("X", 1, "X", 2);

        var ex = Invoking(() => new UpcasterChain([upcaster, upcaster]))
            .Should().Throw<UpcasterCycleException>().Which;

        ex.EventType.Should().Be("X");
        ex.SchemaVersion.Should().Be(1);
        ex.VisitedPath.Should().BeEmpty();
        ex.Message.Should().Contain("Self-referencing");
    }

    [Fact]
    public void Constructor_ValidSet_CountMatchesRegisteredUpcasters_AndDoesNotThrow()
    {
        IEventUpcaster[] upcasters =
        [
            Linear("X", 1, "X", 2),
            Linear("X", 2, "Y", 1),
            Linear("Z", 1, "Z", 2),
        ];

        var chain = new UpcasterChain(upcasters);

        chain.Count.Should().Be(3);
    }

    [Fact]
    public void Constructor_EmptySequence_CountIsZero_AndDoesNotThrow()
    {
        var chain = new UpcasterChain([]);

        chain.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_NullUpcasters_ThrowsArgumentNullException()
    {
        Invoking(() => new UpcasterChain(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullUpcasterElement_ThrowsArgumentNullException()
    {
        Invoking(() => new UpcasterChain([null!])).Should().Throw<ArgumentNullException>();
    }
}
