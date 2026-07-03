using CsCheck;

using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// CsCheck generators for the dedup property tests (task 2.3 — TESTING-SPEC §6.1 "Dedup (ADR-003)"
/// row). EventIds are drawn from a small fixed pool of deterministic ids (via
/// <see cref="TestEvents.DeterministicId"/>) so that duplicates arise naturally and controllably;
/// the "fresh" ids used by <see cref="PartiallyOverlappingBatches"/> come from a disjoint seed
/// namespace ("fresh-*" vs "evt-*") so a genuinely-new key is always available regardless of how
/// many pool ids a base batch consumed.
/// </summary>
public static class DedupGenerators
{
    /// <summary>Six deterministic, mutually-distinct EventIds — the shared source of dedup keys.</summary>
    private static readonly Guid[] IdPool =
        [.. Enumerable.Range(0, 6).Select(i => TestEvents.DeterministicId($"evt-{i}"))];

    /// <summary>Three deterministic EventIds from a namespace disjoint from <see cref="IdPool"/>.</summary>
    private static readonly Guid[] FreshPool =
        [.. Enumerable.Range(0, 3).Select(i => TestEvents.DeterministicId($"fresh-{i}"))];

    /// <summary>
    /// A batch of 1..6 events with mutually-distinct EventIds — selected <b>without replacement</b>
    /// from <see cref="IdPool"/> (GAP-2: a with-replacement draw from a 6-value pool would not
    /// guarantee uniqueness for longer batches).
    /// </summary>
    public static readonly Gen<EventData[]> DistinctBatch =
        Gen.Shuffle(IdPool, 1, IdPool.Length).Select(ToEvents);

    /// <summary>
    /// A batch (length ≥ 2) carrying at least one intra-batch duplicate EventId: a distinct draw
    /// with one of its ids repeated at the end. Feeds edge (a).
    /// </summary>
    public static readonly Gen<EventData[]> BatchWithIntraDuplicate =
        from ids in Gen.Shuffle(IdPool, 1, IdPool.Length)
        from dup in Gen.Int[0, ids.Length - 1]
        select ToEvents([.. ids, ids[dup]]);

    /// <summary>
    /// A pair (first, partialRetry) where <c>partialRetry</c> overlaps <c>first</c> on ≥ 1 key
    /// <b>and</b> carries ≥ 1 genuinely-fresh key from the disjoint <see cref="FreshPool"/>
    /// (GAP-3: the fresh part must exist independently of how much of <see cref="IdPool"/> the base
    /// batch consumed). Feeds edge (d) / GAP-1.
    /// </summary>
    public static readonly Gen<(EventData[] first, EventData[] partialRetry)> PartiallyOverlappingBatches =
        from ids in Gen.Shuffle(IdPool, 1, IdPool.Length)
        from overlapCount in Gen.Int[1, ids.Length]
        from freshCount in Gen.Int[1, FreshPool.Length]
        select (ToEvents(ids), ToEvents([.. ids.Take(overlapCount), .. FreshPool.Take(freshCount)]));

    /// <summary>
    /// Every ExpectedVersion mode worth exercising for edge (c): the five sentinels plus exact
    /// versions — including <c>999</c>, which always mismatches so the "dedup precedes the guard"
    /// invariant is genuinely probed (a mode whose guard would otherwise fail).
    /// </summary>
    public static readonly Gen<long> ExpectedVersionMode = Gen.OneOfConst(
        ExpectedVersion.Any,
        ExpectedVersion.NoStream,
        ExpectedVersion.EmptyStream,
        ExpectedVersion.StreamExists,
        1L,
        5L,
        999L);

    private static EventData[] ToEvents(Guid[] ids) => [.. ids.Select(id => TestEvents.OrderPlaced(id))];
}
