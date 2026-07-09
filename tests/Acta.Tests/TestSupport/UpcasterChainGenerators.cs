using CsCheck;

using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// CsCheck generators for the <c>UpcasterChain</c> property tests (task 8.2 — TESTING-SPEC §6.1
/// "Upcasting (FR-8)" row, NR3 part 3). Every generator builds a walk that is acyclic <b>by
/// construction</b>: each hop uses a unique <see cref="IEventUpcaster.EventType"/> (<c>"T0" →
/// "T1" → … → "Tk"</c>), so no <c>(EventType, FromSchemaVersion)</c> key can ever repeat on a
/// path — the "never throws" properties (<see cref="Upcasting.Properties.UpcasterChainPropertyTests"/>)
/// exercise genuine totality, never a self-inflicted <c>UpcasterCycleException</c>.
/// </summary>
public static class UpcasterChainGenerators
{
    /// <summary>Registered <c>FromSchemaVersion</c> candidates for <see cref="MissingUpcasterForOldVersion"/> — all &gt;= 2, so <c>OldVersion = 1</c> is always unregistered.</summary>
    private static readonly int[] OldVersionPool = [2, 3, 4, 5, 6];

    /// <summary>
    /// A linear, cross-type chain of length <c>k ∈ [1,6]</c>: unique types <c>T0..Tk</c>, versions
    /// strictly increasing (<c>i+1</c>), one upcaster per hop (<c>Ti@(i+1) → T(i+1)@(i+2)</c>).
    /// Head is <c>(T0, 1)</c>; terminal is <c>(Tk, k+1)</c> — a key that, by construction, is never
    /// itself registered (it only ever appears as a produced target, never as a source), so it also
    /// serves as the "already at its newest version" probe for pass-through properties.
    /// </summary>
    public static readonly Gen<LinearChainCase> LinearCrossTypeChain =
        Gen.Int[1, 6].Select(BuildLinearChain);

    /// <summary>
    /// A single-hop 1:N fan-out: head <c>("H", 1)</c>; <c>n ∈ [2,5]</c> unique terminal types
    /// <c>F0..F(n-1)</c>, each at schema version 1.
    /// </summary>
    public static readonly Gen<FanOutCase> FanOut =
        Gen.Int[2, 5].Select(BuildFanOut);

    /// <summary>
    /// A single event type <c>"T"</c> with a non-empty, randomly-sized subset of registered
    /// <c>FromSchemaVersion</c>s drawn from <see cref="OldVersionPool"/> (all &gt;= 2) and
    /// <c>OldVersion = 1</c>, which is therefore always below every registered version — an
    /// unregistered key, guaranteed never to collide with a registered one.
    /// </summary>
    public static readonly Gen<MissingOldVersionCase> MissingUpcasterForOldVersion =
        Gen.Shuffle(OldVersionPool, 1, OldVersionPool.Length).Select(BuildMissingOldVersion);

    /// <summary>Raw-JSON counterpart of <see cref="LinearCrossTypeChain"/> (parity walk, D3 homogeneity).</summary>
    public static readonly Gen<LinearChainCase> RawLinearCrossTypeChain =
        Gen.Int[1, 6].Select(BuildRawLinearChain);

    /// <summary>Raw-JSON counterpart of <see cref="FanOut"/> (parity walk, D3 homogeneity).</summary>
    public static readonly Gen<FanOutCase> RawFanOut =
        Gen.Int[2, 5].Select(BuildRawFanOut);

    private static LinearChainCase BuildLinearChain(int length)
    {
        var upcasters = new List<IEventUpcaster>(length);
        for (var i = 0; i < length; i++)
            upcasters.Add(Linear($"T{i}", i + 1, $"T{i + 1}", i + 2));

        return new LinearChainCase(upcasters, HeadType: "T0", HeadVersion: 1, TerminalType: $"T{length}", TerminalVersion: length + 1);
    }

    private static LinearChainCase BuildRawLinearChain(int length)
    {
        var upcasters = new List<IEventUpcaster>(length);
        for (var i = 0; i < length; i++)
            upcasters.Add(LinearRaw($"T{i}", i + 1, $"T{i + 1}", i + 2));

        return new LinearChainCase(upcasters, HeadType: "T0", HeadVersion: 1, TerminalType: $"T{length}", TerminalVersion: length + 1);
    }

    private static FanOutCase BuildFanOut(int width)
    {
        var terminals = new List<(string Type, int Version)>(width);
        for (var i = 0; i < width; i++)
            terminals.Add(($"F{i}", 1));

        var head = new ConfigurableEventUpcaster
        {
            EventType = "H",
            FromSchemaVersion = 1,
            Transform = (e, _) => [.. terminals.Select(t => new UpcastedEvent(e, t.Type, t.Version))],
        };

        return new FanOutCase(head, HeadType: "H", HeadVersion: 1, terminals);
    }

    private static FanOutCase BuildRawFanOut(int width)
    {
        var terminals = new List<(string Type, int Version)>(width);
        for (var i = 0; i < width; i++)
            terminals.Add(($"F{i}", 1));

        var head = new ConfigurableRawJsonEventUpcaster
        {
            EventType = "H",
            FromSchemaVersion = 1,
            TransformRaw = (payload, _) => [.. terminals.Select(t => new RawUpcastedEvent(payload, t.Type, t.Version))],
        };

        return new FanOutCase(head, HeadType: "H", HeadVersion: 1, terminals);
    }

    private static MissingOldVersionCase BuildMissingOldVersion(int[] registeredVersions)
    {
        var upcasters = registeredVersions.Select(v => Linear("T", v, "T", v + 1)).ToList<IEventUpcaster>();
        return new MissingOldVersionCase(upcasters, Type: "T", OldVersion: 1);
    }

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
}

/// <summary>
/// A linear, cross-type upcaster chain case: <see cref="Upcasters"/> walks from
/// <c>(HeadType, HeadVersion)</c> to a guaranteed-unregistered <c>(TerminalType, TerminalVersion)</c>.
/// </summary>
/// <param name="Upcasters">The chain's registered upcasters, in hop order.</param>
/// <param name="HeadType">The starting event type.</param>
/// <param name="HeadVersion">The starting schema version.</param>
/// <param name="TerminalType">The event type the walk reaches (no upcaster is registered for it).</param>
/// <param name="TerminalVersion">The schema version the walk reaches (no upcaster is registered for it).</param>
public sealed record LinearChainCase(IReadOnlyList<IEventUpcaster> Upcasters, string HeadType, int HeadVersion, string TerminalType, int TerminalVersion);

/// <summary>A single-hop 1:N fan-out upcaster chain case: <see cref="Head"/> alone maps <c>(HeadType, HeadVersion)</c> to every entry of <see cref="Terminals"/>.</summary>
/// <param name="Head">The single registered upcaster performing the fan-out.</param>
/// <param name="HeadType">The starting event type.</param>
/// <param name="HeadVersion">The starting schema version.</param>
/// <param name="Terminals">The guaranteed-unregistered <c>(EventType, SchemaVersion)</c> keys produced by the fan-out.</param>
public sealed record FanOutCase(IEventUpcaster Head, string HeadType, int HeadVersion, IReadOnlyList<(string Type, int Version)> Terminals);

/// <summary>A chain case exercising "no upcaster registered for an old schema version": <see cref="OldVersion"/> is below every version registered for <see cref="Type"/>.</summary>
/// <param name="Upcasters">The chain's registered upcasters, none of which claims <see cref="OldVersion"/>.</param>
/// <param name="Type">The event type under test.</param>
/// <param name="OldVersion">A schema version strictly below every registered <c>FromSchemaVersion</c> for <see cref="Type"/> — guaranteed unregistered.</param>
public sealed record MissingOldVersionCase(IReadOnlyList<IEventUpcaster> Upcasters, string Type, int OldVersion);
