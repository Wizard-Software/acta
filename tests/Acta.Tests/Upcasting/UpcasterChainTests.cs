using System.Text;

using Xunit;

using Acta.Abstractions;
using Acta.Tests.TestSupport;
using Acta.Upcasting;

namespace Acta.Tests.Upcasting;

public sealed class UpcasterChainTests
{
    private sealed record Marker(string Label);

    private static EventMetadata AnyMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static ConfigurableEventUpcaster Linear(string eventType, int fromSchemaVersion, string toEventType, int toSchemaVersion) => new()
    {
        EventType = eventType,
        FromSchemaVersion = fromSchemaVersion,
        Transform = (e, _) => [new UpcastedEvent(e, toEventType, toSchemaVersion)],
    };

    private static ConfigurableRawJsonEventUpcaster LinearRaw(string eventType, int fromSchemaVersion, string toEventType, int toSchemaVersion) => new()
    {
        EventType = eventType,
        FromSchemaVersion = fromSchemaVersion,
        TransformRaw = (payload, _) => [new RawUpcastedEvent(payload, toEventType, toSchemaVersion)],
    };

    // ---- Object walk (Upcast) --------------------------------------------------------------

    [Fact]
    public void Upcast_LinearSameTypeStep_ReturnsSingleEventAtNextVersion()
    {
        var chain = new UpcasterChain([Linear("X", 1, "X", 2)]);
        var input = new Marker("v1");

        var result = chain.Upcast(input, "X", 1, AnyMetadata());

        result.Should().ContainSingle();
        result[0].EventType.Should().Be("X");
        result[0].SchemaVersion.Should().Be(2);
    }

    [Fact]
    public void Upcast_CrossTypeStep_ContinuesWalkKeyedOnNewEventType()
    {
        var chain = new UpcasterChain([Linear("X", 1, "Y", 1)]);
        var input = new Marker("v1");

        var result = chain.Upcast(input, "X", 1, AnyMetadata());

        result.Should().ContainSingle();
        result[0].EventType.Should().Be("Y");
        result[0].SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void Upcast_FanOut1ToN_BothBranchesTerminateIndependently()
    {
        var chain = new UpcasterChain([
            new ConfigurableEventUpcaster
            {
                EventType = "X",
                FromSchemaVersion = 1,
                Transform = (e, _) => [new UpcastedEvent(e, "Y", 1), new UpcastedEvent(e, "Z", 1)],
            },
        ]);
        var input = new Marker("v1");

        var result = chain.Upcast(input, "X", 1, AnyMetadata());

        result.Should().HaveCount(2);
        result.Should().Contain(u => u.EventType == "Y" && u.SchemaVersion == 1);
        result.Should().Contain(u => u.EventType == "Z" && u.SchemaVersion == 1);
    }

    [Fact]
    public void Upcast_NoUpcasterRegisteredForInputKey_ReturnsInputUnchanged()
    {
        var chain = new UpcasterChain([Linear("X", 1, "X", 2)]);
        var input = new Marker("already-current");

        var result = chain.Upcast(input, "X", 3, AnyMetadata());

        result.Should().ContainSingle();
        result[0].Event.Should().BeSameAs(input);
        result[0].EventType.Should().Be("X");
        result[0].SchemaVersion.Should().Be(3);
    }

    [Fact]
    public void Upcast_OperationalCrossTypeCycle_ThrowsUpcasterCycleException()
    {
        var chain = new UpcasterChain([
            Linear("X", 1, "Y", 1),
            Linear("Y", 1, "X", 1),
        ]);
        var input = new Marker("v1");

        var ex = Invoking(() => chain.Upcast(input, "X", 1, AnyMetadata()))
            .Should().Throw<UpcasterCycleException>().Which;

        ex.EventType.Should().Be("X");
        ex.SchemaVersion.Should().Be(1);
        ex.VisitedPath.Should().Equal(("X", 1), ("Y", 1), ("X", 1));
    }

    [Fact]
    public void Upcast_LinearChainSpanningAllRegisteredUpcasters_DoesNotExceedBranchDepthLimit()
    {
        // The per-branch hard iteration limit is Count + 1 (defense-in-depth backstop against
        // divergence). A legitimate, non-cyclic linear chain that walks through every one of the
        // Count registered upcasters must NOT be rejected by that limit — this is the boundary the
        // limit must accommodate. (A branch that genuinely exceeds Count + 1 hops without
        // terminating would necessarily have revisited an already-registered key — since only
        // Count distinct keys exist — so it is always also caught by the operational cycle guard
        // first; see Upcast_OperationalCrossTypeCycle_ThrowsUpcasterCycleException.)
        const int hopCount = 5;
        var upcasters = new List<IEventUpcaster>();
        for (var version = 1; version <= hopCount; version++)
            upcasters.Add(Linear("X", version, "X", version + 1));

        var chain = new UpcasterChain(upcasters);
        var input = new Marker("v1");

        var result = chain.Upcast(input, "X", 1, AnyMetadata());

        result.Should().ContainSingle();
        result[0].SchemaVersion.Should().Be(hopCount + 1);
    }

    [Fact]
    public void Upcast_DiamondLatticeFanOut_GlobalWorkBudgetThrows()
    {
        // A converging "diamond" lattice: every level has two schema-version nodes, and BOTH of
        // them fan out into the very SAME two nodes at the next level. No single path ever
        // revisits a key (each path's schema versions strictly increase level by level), so the
        // per-path cycle guard and the per-branch depth limit (Count + 1, comfortably above the
        // handful of levels reached before the budget trips) never fire — only the GLOBAL
        // per-invocation work budget (Q4) can stop the combinatorial ~2^Count blow-up, because the
        // total number of queue items doubles at every level even though there are only two
        // distinct keys per level.
        const int levels = 20;
        var upcasters = new List<IEventUpcaster>
        {
            new ConfigurableEventUpcaster
            {
                EventType = "L0",
                FromSchemaVersion = 1,
                Transform = (e, _) => [new UpcastedEvent(e, "L1", 1), new UpcastedEvent(e, "L1", 2)],
            },
        };

        for (var level = 1; level < levels; level++)
        {
            var nextLevel = level + 1;
            foreach (var schemaVersion in new[] { 1, 2 })
            {
                upcasters.Add(new ConfigurableEventUpcaster
                {
                    EventType = $"L{level}",
                    FromSchemaVersion = schemaVersion,
                    Transform = (e, _) => [new UpcastedEvent(e, $"L{nextLevel}", 1), new UpcastedEvent(e, $"L{nextLevel}", 2)],
                });
            }
        }

        var chain = new UpcasterChain(upcasters);
        var input = new Marker("v1");

        var ex = Invoking(() => chain.Upcast(input, "L0", 1, AnyMetadata()))
            .Should().Throw<UpcasterCycleException>().Which;

        ex.Message.Should().Contain("work budget");
    }

    [Fact]
    public void Upcast_NullEvent_ThrowsArgumentNullException()
    {
        var chain = new UpcasterChain([]);

        Invoking(() => chain.Upcast(null!, "X", 1, AnyMetadata())).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Upcast_NullMetadata_ThrowsArgumentNullException()
    {
        var chain = new UpcasterChain([]);

        Invoking(() => chain.Upcast(new Marker("v1"), "X", 1, null!)).Should().Throw<ArgumentNullException>();
    }

    // ---- Raw-JSON walk (UpcastRaw) ---------------------------------------------------------

    [Fact]
    public void UpcastRaw_LinearSameTypeStep_ReturnsSinglePayloadAtNextVersion()
    {
        var chain = new UpcasterChain([LinearRaw("X", 1, "X", 2)]);
        var payload = Bytes("{\"v\":1}");

        var result = chain.UpcastRaw("X", 1, payload, AnyMetadata());

        result.Should().ContainSingle();
        result[0].EventType.Should().Be("X");
        result[0].SchemaVersion.Should().Be(2);
    }

    [Fact]
    public void UpcastRaw_CrossTypeStep_ContinuesWalkKeyedOnNewEventType()
    {
        var chain = new UpcasterChain([LinearRaw("X", 1, "Y", 1)]);
        var payload = Bytes("{\"v\":1}");

        var result = chain.UpcastRaw("X", 1, payload, AnyMetadata());

        result.Should().ContainSingle();
        result[0].EventType.Should().Be("Y");
        result[0].SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void UpcastRaw_FanOut1ToN_BothBranchesTerminateIndependently()
    {
        var chain = new UpcasterChain([
            new ConfigurableRawJsonEventUpcaster
            {
                EventType = "X",
                FromSchemaVersion = 1,
                TransformRaw = (payload, _) => [new RawUpcastedEvent(payload, "Y", 1), new RawUpcastedEvent(payload, "Z", 1)],
            },
        ]);
        var payload = Bytes("{\"v\":1}");

        var result = chain.UpcastRaw("X", 1, payload, AnyMetadata());

        result.Should().HaveCount(2);
        result.Should().Contain(u => u.EventType == "Y" && u.SchemaVersion == 1);
        result.Should().Contain(u => u.EventType == "Z" && u.SchemaVersion == 1);
    }

    [Fact]
    public void UpcastRaw_NoUpcasterRegisteredForInputKey_ReturnsInputPayloadUnchanged()
    {
        var chain = new UpcasterChain([LinearRaw("X", 1, "X", 2)]);
        var payload = Bytes("already-current");

        var result = chain.UpcastRaw("X", 3, payload, AnyMetadata());

        result.Should().ContainSingle();
        result[0].PayloadJson.ToArray().Should().Equal(payload.ToArray());
        result[0].EventType.Should().Be("X");
        result[0].SchemaVersion.Should().Be(3);
    }

    [Fact]
    public void UpcastRaw_NonRawStepEncountered_ThrowsInvalidOperationException()
    {
        var chain = new UpcasterChain([
            LinearRaw("X", 1, "Y", 1),
            new ConfigurableEventUpcaster
            {
                EventType = "Y",
                FromSchemaVersion = 1,
                Transform = (e, _) => [new UpcastedEvent(e, "Y", 2)],
            },
        ]);

        Invoking(() => chain.UpcastRaw("X", 1, Bytes("{\"v\":1}"), AnyMetadata()))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void UpcastRaw_NullMetadata_ThrowsArgumentNullException()
    {
        var chain = new UpcasterChain([]);

        Invoking(() => chain.UpcastRaw("X", 1, ReadOnlyMemory<byte>.Empty, null!)).Should().Throw<ArgumentNullException>();
    }
}
