using Xunit;

using Acta.Upcasting;

namespace Acta.Tests.Upcasting;

/// <summary>
/// Direct unit tests for <see cref="UpcasterCycleException"/>'s message-construction path (task 8.1,
/// SEC-2/ADR-011 diagnostic-only shape): every factory method is exercised directly (the type's
/// factories are <see langword="internal"/>, reachable via <c>InternalsVisibleTo</c>) with exact
/// <see cref="Exception.Message"/> assertions — not merely "an exception was thrown" — so that a
/// string mutation, a dropped path segment, or a broken separator/format in
/// <c>FormatPath</c> is observably wrong, regardless of whether <see cref="UpcasterChain"/> itself
/// happens to reach every factory through its own walk.
/// </summary>
public sealed class UpcasterCycleExceptionTests
{
    [Fact]
    public void ForDuplicateKey_MessageNamesEventTypeAndSchemaVersion()
    {
        var ex = UpcasterCycleException.ForDuplicateKey("Dup-X", 3);

        ex.Message.Should().Be(
            "Ambiguous upcaster chain: more than one upcaster is registered for event type " +
            "'Dup-X' at schema version 3.");
        ex.EventType.Should().Be("Dup-X");
        ex.SchemaVersion.Should().Be(3);
        ex.VisitedPath.Should().BeEmpty();
    }

    [Fact]
    public void ForSelfReference_MessageNamesEventTypeAndSchemaVersion()
    {
        var ex = UpcasterCycleException.ForSelfReference("Self-X", 5);

        ex.Message.Should().Be(
            "Self-referencing upcaster chain: the same upcaster instance is registered more than " +
            "once for event type 'Self-X' at schema version 5.");
        ex.EventType.Should().Be("Self-X");
        ex.SchemaVersion.Should().Be(5);
        ex.VisitedPath.Should().BeEmpty();
    }

    [Fact]
    public void ForCycle_WithMultiHopPath_MessageRendersPathArrowJoinedInOrder()
    {
        // Exercises FormatPath's non-empty branch AND its per-hop "EventType:SchemaVersion"
        // format AND its " -> " separator in one exact match — a mutation to any of the three
        // (the ternary's condition, the per-hop interpolation, or the separator) breaks this.
        var ex = UpcasterCycleException.ForCycle("Cyc-X", 2, [("Cyc-X", 2), ("Cyc-Y", 1), ("Cyc-X", 2)]);

        ex.Message.Should().Be(
            "Upcaster chain cycle detected: 'Cyc-X' at schema version 2 is " +
            "revisited on a single conversion path (Cyc-X:2 -> Cyc-Y:1 -> Cyc-X:2).");
        ex.VisitedPath.Should().Equal(("Cyc-X", 2), ("Cyc-Y", 1), ("Cyc-X", 2));
    }

    [Fact]
    public void ForCycle_WithEmptyPath_MessageUsesNoPathRecordedPlaceholder()
    {
        // FormatPath's Count == 0 branch — only reachable through this internal factory directly
        // (a real walk's cycle path always has at least one hop), but the factory's own contract
        // must still degrade to the placeholder rather than an empty/blank rendering.
        var ex = UpcasterCycleException.ForCycle("Cyc-Empty", 9, []);

        ex.Message.Should().Be(
            "Upcaster chain cycle detected: 'Cyc-Empty' at schema version 9 is " +
            "revisited on a single conversion path ((no path recorded)).");
    }

    [Fact]
    public void ForBranchDepthExceeded_MessageNamesDepthLimitEventTypeVersionAndPath()
    {
        var ex = UpcasterCycleException.ForBranchDepthExceeded("Depth-X", 9, 6, [("A", 1), ("B", 2)]);

        ex.Message.Should().Be(
            "Upcaster chain branch exceeded its hard iteration limit (6) without " +
            "terminating at 'Depth-X' schema version 9 (A:1 -> B:2).");
        ex.EventType.Should().Be("Depth-X");
        ex.SchemaVersion.Should().Be(9);
        ex.VisitedPath.Should().Equal(("A", 1), ("B", 2));
    }

    [Fact]
    public void ForWorkBudgetExceeded_MessageNamesBudgetEventTypeVersionAndPath()
    {
        var ex = UpcasterCycleException.ForWorkBudgetExceeded("Budget-X", 4, 128, [("A", 1), ("B", 2)]);

        ex.Message.Should().Be(
            "Upcaster chain exceeded its global per-invocation work budget (128 steps) " +
            "while converting toward 'Budget-X' schema version 4 — likely a " +
            "combinatorial fan-out (A:1 -> B:2).");
        ex.EventType.Should().Be("Budget-X");
        ex.SchemaVersion.Should().Be(4);
        ex.VisitedPath.Should().Equal(("A", 1), ("B", 2));
    }
}
