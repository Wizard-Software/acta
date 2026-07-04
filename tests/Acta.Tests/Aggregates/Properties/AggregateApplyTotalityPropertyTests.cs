using CsCheck;
using Xunit;

using Acta.Tests.TestSupport;

namespace Acta.Tests.Aggregates.Properties;

/// <summary>
/// Property test for the "Totalność `Apply`" row of the TESTING-SPEC §6.1 edge-case catalogue
/// (task 3.1 — NR3 part 3), exercised against <see cref="CounterAggregate"/> via CsCheck.
/// <para>
/// AK-4 / FR-11: <c>Apply</c> NEVER throws — an unknown event type is a no-op, not a fault. Each
/// property iteration builds its OWN <see cref="CounterAggregate"/> instance: CsCheck runs samples
/// in parallel, so a shared aggregate would race across iterations. The property test itself is
/// synchronous — folding is pure CPU work with no I/O, so <c>Sample</c> (not <c>SampleAsync</c>)
/// applies and no <see cref="CancellationToken"/> is involved.
/// </para>
/// <para>
/// Boundary facts explicit in §6.1 (empty stream, single event, 100k+ stream) are pinned alongside
/// the property as deterministic <see cref="FactAttribute"/> cases (plan D2): a single scale/iterativity
/// check is cheaper and clearer than fuzzing scale via CsCheck's sample loop.
/// </para>
/// </summary>
public sealed class AggregateApplyTotalityPropertyTests
{
    [Fact]
    public void Property_LoadFromHistory_IsTotal_NeverThrows_AndTracksVersion()
    {
        AggregateEventGenerators.History.Sample(history =>
        {
            var aggregate = new CounterAggregate();

            var exception = Record.Exception(() => aggregate.LoadFromHistory(history));

            exception.Should().BeNull();
            aggregate.Version.Should().Be(history.Length - 1);
            aggregate.UncommittedEvents.Should().BeEmpty();
            aggregate.Ignored.Should().Be(history.Count(e => e is UnknownEvent));
        });
    }

    [Fact]
    public void LoadFromHistory_EmptyStream_VersionStaysMinusOne()
    {
        var aggregate = new CounterAggregate();

        aggregate.LoadFromHistory([]);

        aggregate.Version.Should().Be(-1);
    }

    [Fact]
    public void LoadFromHistory_SingleEvent_VersionIsZero()
    {
        var aggregate = new CounterAggregate();

        aggregate.LoadFromHistory([new Incremented()]);

        aggregate.Version.Should().Be(0);
    }

    [Fact]
    public void LoadFromHistory_HundredThousandEvents_DoesNotThrow_AndVersionIsCountMinusOne()
    {
        const int EventCount = 100_000;
        var aggregate = new CounterAggregate();
        var history = new object[EventCount];
        for (var i = 0; i < EventCount; i++)
        {
            history[i] = i % 2 == 0 ? new Incremented() : new UnknownEvent(i);
        }

        // Iterative fold (LoadFromHistory uses `foreach`, not recursion) — a stack-overflow at this
        // scale would indicate a regression to a recursive implementation.
        var exception = Record.Exception(() => aggregate.LoadFromHistory(history));

        exception.Should().BeNull();
        aggregate.Version.Should().Be(EventCount - 1);
    }
}
