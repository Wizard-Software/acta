using System.Text;

using CsCheck;
using Xunit;

using Acta.Abstractions;
using Acta.Tests.TestSupport;
using Acta.Upcasting;

namespace Acta.Tests.Upcasting.Properties;

/// <summary>
/// Property tests for the "Upcasting (FR-8)" row of the TESTING-SPEC §6.1 edge-case catalogue
/// (task 8.2 — NR3 part 3), exercised against <see cref="UpcasterChain"/> via CsCheck. The chain's
/// walk (<see cref="UpcasterChain.Upcast"/> / <see cref="UpcasterChain.UpcastRaw"/>) is synchronous
/// pure in-memory dictionary/queue work — <c>Sample</c> (not <c>SampleAsync</c>) applies throughout,
/// and no <see cref="CancellationToken"/> is involved.
/// <para>
/// Four edges of §6.1 are covered by one dedicated, independently-failable property each (P1–P4):
/// cross-type conversion, 1:N fan-out, "no upcaster registered for an old schema version" (a
/// contract gap, not a crash), and "event already at its newest version". P5–P8 are reinforcing
/// universal invariants (terminality, acyclic totality, determinism, and raw/object walk parity)
/// following the project's established NR3 pattern (<see cref="Store.Properties.InMemoryDedupPropertyTests"/>,
/// <see cref="Aggregates.Properties.AggregateApplyTotalityPropertyTests"/>).
/// </para>
/// <para>
/// <see cref="UpcasterChain"/> is immutable after construction (built once from a generated case,
/// never mutated), so it may be safely shared across the assertions inside one iteration; each
/// generated payload/case is itself immutable, so nothing here races across CsCheck's parallel
/// samples.
/// </para>
/// </summary>
public sealed class UpcasterChainPropertyTests
{
    private sealed record Payload(string Label);

    private static EventMetadata AnyMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

    // ---- P1: cross-type — the walk continues keyed on the produced event type -------------

    [Fact]
    public void Property_CrossTypeLinearChain_WalksToTerminalTypeAndVersion()
    {
        UpcasterChainGenerators.LinearCrossTypeChain.Sample(@case =>
        {
            var chain = new UpcasterChain(@case.Upcasters);
            var input = new Payload("head");

            var result = chain.Upcast(input, @case.HeadType, @case.HeadVersion, AnyMetadata());

            result.Should().ContainSingle();
            result[0].EventType.Should().Be(@case.TerminalType);
            result[0].SchemaVersion.Should().Be(@case.TerminalVersion);
            result[0].Event.Should().BeSameAs(input);
        });
    }

    // ---- P2: 1:N fan-out — every branch terminates independently, payload untouched -------

    [Fact]
    public void Property_FanOut1ToN_ProducesNTerminalsPreservingPayload()
    {
        UpcasterChainGenerators.FanOut.Sample(@case =>
        {
            var chain = new UpcasterChain([@case.Head]);
            var input = new Payload("head");

            var result = chain.Upcast(input, @case.HeadType, @case.HeadVersion, AnyMetadata());

            result.Should().HaveCount(@case.Terminals.Count);
            var expectedKeys = @case.Terminals.Select(t => (t.Type, t.Version)).ToHashSet();
            var actualKeys = result.Select(r => (r.EventType, r.SchemaVersion)).ToHashSet();
            actualKeys.Should().BeEquivalentTo(expectedKeys);
            result.Should().OnlyContain(r => ReferenceEquals(r.Event, input));
        });
    }

    // ---- P3: no upcaster registered for an old schema version — contract gap, not a crash -

    [Fact]
    public void Property_MissingUpcasterForOldVersion_ReturnsInputUnchanged_DoesNotThrow()
    {
        UpcasterChainGenerators.MissingUpcasterForOldVersion.Sample(@case =>
        {
            var chain = new UpcasterChain(@case.Upcasters);
            var input = new Payload("old");

            var result = chain.Upcast(input, @case.Type, @case.OldVersion, AnyMetadata());

            result.Should().ContainSingle();
            result[0].Event.Should().BeSameAs(input);
            result[0].EventType.Should().Be(@case.Type);
            result[0].SchemaVersion.Should().Be(@case.OldVersion);
        });
    }

    // ---- P4: event already at its newest version — fast-path pass-through -----------------

    [Fact]
    public void Property_EventAlreadyAtNewestVersion_ReturnsInputUnchanged()
    {
        UpcasterChainGenerators.LinearCrossTypeChain.Sample(@case =>
        {
            // The terminal key is never itself a registered source (only ever a produced target),
            // so probing it exercises the "already at its current schema version" fast path.
            var chain = new UpcasterChain(@case.Upcasters);
            var input = new Payload("terminal");

            var result = chain.Upcast(input, @case.TerminalType, @case.TerminalVersion, AnyMetadata());

            result.Should().ContainSingle();
            result[0].Event.Should().BeSameAs(input);
            result[0].EventType.Should().Be(@case.TerminalType);
            result[0].SchemaVersion.Should().Be(@case.TerminalVersion);
        });
    }

    // ---- P5: reinforcement — every walk result is genuinely terminal -----------------------

    [Fact]
    public void Property_UpcastResult_IsAlwaysTerminal()
    {
        UpcasterChainGenerators.LinearCrossTypeChain.Sample(@case =>
        {
            var chain = new UpcasterChain(@case.Upcasters);
            var registeredKeys = @case.Upcasters.Select(u => (u.EventType, u.FromSchemaVersion)).ToHashSet();

            var result = chain.Upcast(new Payload("head"), @case.HeadType, @case.HeadVersion, AnyMetadata());

            // A plain delegate (not an expression tree) is required here: tuple literals cannot
            // appear inside an `Expression<Func<T, bool>>`, which is what `OnlyContain` builds.
            result.All(r => !registeredKeys.Contains((r.EventType, r.SchemaVersion))).Should().BeTrue();
        });

        UpcasterChainGenerators.FanOut.Sample(@case =>
        {
            var chain = new UpcasterChain([@case.Head]);
            var registeredKey = (@case.Head.EventType, @case.Head.FromSchemaVersion);

            var result = chain.Upcast(new Payload("head"), @case.HeadType, @case.HeadVersion, AnyMetadata());

            result.All(r => (r.EventType, r.SchemaVersion) != registeredKey).Should().BeTrue();
        });
    }

    // ---- P6: reinforcement — an acyclic chain never throws (totality) ----------------------

    [Fact]
    public void Property_AcyclicChain_NeverThrows()
    {
        UpcasterChainGenerators.LinearCrossTypeChain.Sample(@case =>
        {
            var chain = new UpcasterChain(@case.Upcasters);

            var exception = Record.Exception(() => chain.Upcast(new Payload("head"), @case.HeadType, @case.HeadVersion, AnyMetadata()));

            exception.Should().BeNull();
        });
    }

    // ---- P7: reinforcement — the walk is deterministic -------------------------------------

    [Fact]
    public void Property_Upcast_IsDeterministic()
    {
        UpcasterChainGenerators.LinearCrossTypeChain.Sample(@case =>
        {
            var chain = new UpcasterChain(@case.Upcasters);
            var metadata = AnyMetadata();
            var input = new Payload("same");

            var first = chain.Upcast(input, @case.HeadType, @case.HeadVersion, metadata);
            var second = chain.Upcast(input, @case.HeadType, @case.HeadVersion, metadata);

            first.Should().HaveCount(second.Count);
            first.Select(r => (r.EventType, r.SchemaVersion)).Should()
                .BeEquivalentTo(second.Select(r => (r.EventType, r.SchemaVersion)));
        });
    }

    // ---- P8: raw/object walk parity — fan-out and pass-through preserve payload bytes ------

    [Fact]
    public void Property_UpcastRaw_MirrorsObjectWalk_ForFanOutAndPassThrough()
    {
        UpcasterChainGenerators.RawFanOut.Sample(@case =>
        {
            var chain = new UpcasterChain([@case.Head]);
            var payload = Bytes("{\"v\":1}");

            var result = chain.UpcastRaw(@case.HeadType, @case.HeadVersion, payload, AnyMetadata());

            result.Should().HaveCount(@case.Terminals.Count);
            var expectedKeys = @case.Terminals.Select(t => (t.Type, t.Version)).ToHashSet();
            var actualKeys = result.Select(r => (r.EventType, r.SchemaVersion)).ToHashSet();
            actualKeys.Should().BeEquivalentTo(expectedKeys);
            result.Should().OnlyContain(r => r.PayloadJson.ToArray().SequenceEqual(payload.ToArray()));
        });

        UpcasterChainGenerators.RawLinearCrossTypeChain.Sample(@case =>
        {
            var chain = new UpcasterChain(@case.Upcasters);
            var payload = Bytes("already-current");

            var result = chain.UpcastRaw(@case.TerminalType, @case.TerminalVersion, payload, AnyMetadata());

            result.Should().ContainSingle();
            result[0].PayloadJson.ToArray().Should().Equal(payload.ToArray());
            result[0].EventType.Should().Be(@case.TerminalType);
            result[0].SchemaVersion.Should().Be(@case.TerminalVersion);
        });
    }
}
