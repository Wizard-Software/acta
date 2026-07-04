using CsCheck;

namespace Acta.Tests.TestSupport;

/// <summary>
/// CsCheck generators for the <c>Apply</c>-totality property test (task 3.1 — TESTING-SPEC §6.1
/// "Totalność `Apply`" row): a mix of <see cref="CounterAggregate"/>-known events
/// (<see cref="Incremented"/>/<see cref="Decremented"/>) and an unknown one
/// (<see cref="UnknownEvent"/>), and histories built from that mix.
/// </summary>
public static class AggregateEventGenerators
{
    private static readonly Gen<object> IncrementedGen = Gen.Const<object>(() => new Incremented());
    private static readonly Gen<object> DecrementedGen = Gen.Const<object>(() => new Decremented());
    private static readonly Gen<object> UnknownEventGen = Gen.Int.Select(payload => (object)new UnknownEvent(payload));

    /// <summary>One of the three event shapes <see cref="CounterAggregate"/> can encounter: two it
    /// recognizes and one it does not (the totality no-op branch).</summary>
    public static readonly Gen<object> AnyEvent = Gen.OneOf(IncrementedGen, DecrementedGen, UnknownEventGen);

    /// <summary>A candidate event history — length 0..200, covering the empty and single-event
    /// boundaries alongside a fuzzed mix of known/unknown event types.</summary>
    public static readonly Gen<object[]> History = AnyEvent.Array[0, 200];
}
