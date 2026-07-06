using CsCheck;

using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// CsCheck generators for the "Kolejność / monotoniczność" row of the TESTING-SPEC §6.1 edge-case
/// catalogue (task 5.5 — NR3 part 2): sequences of <see cref="GlobalPosition"/> with gaps
/// (single / serial / at the end of a batch), gap-reaching-HWM pairs that drive
/// <see cref="Acta.Projections.Daemon.GapGuard"/>, and non-decreasing checkpoint-save sequences (with
/// duplicates = a re-delivered position after retry, and gaps) that drive the
/// <see cref="ICheckpointSink"/> monotonicity contract.
/// <para>
/// Positions are generated as small <see cref="int"/> increments and folded into strictly ascending
/// (or non-decreasing) <see cref="GlobalPosition"/> sequences: an increment ≥ 2 is a gap, an increment
/// of 0 (checkpoint saves only) is a duplicate position. Because every property holds for the full
/// generated shape space, the three named gap shapes (single, serial, end-of-batch) are covered
/// intrinsically; they are also pinned as explicit facts in the property test classes for auditability
/// against the §6.1 wording.
/// </para>
/// </summary>
public static class GlobalPositionSequenceGenerators
{
    /// <summary>
    /// A strictly ascending sequence of <see cref="GlobalPosition"/> built from a start offset and
    /// positive increments in <c>[1, 5]</c> — an increment of 1 is contiguous, an increment ≥ 2 is a
    /// gap. Over the sample space this yields single gaps (one increment of 2 among 1s), serial gaps
    /// (a large increment), and a gap at the end of the batch (the last increment ≥ 2).
    /// </summary>
    public static readonly Gen<GlobalPosition[]> AscendingWithGaps =
        from start in Gen.Int[0, 20]
        from increments in Gen.Int[1, 5].Array[1, 12]
        select FoldAscending(start, increments);

    /// <summary>
    /// A <c>(checkpoint, safeHwm)</c> pair with <c>checkpoint &lt; safeHwm</c> — a projection whose
    /// checkpoint is trapped under the safe high-water mark (a true hole reaching the HWM, the input
    /// shape <see cref="Acta.Projections.Daemon.GapGuard.Evaluate"/> classifies).
    /// </summary>
    public static readonly Gen<(GlobalPosition Checkpoint, GlobalPosition SafeHwm)> GapReachingHwm =
        from checkpoint in Gen.Int[0, 100]
        from delta in Gen.Int[1, 100]
        select (new GlobalPosition(checkpoint), new GlobalPosition(checkpoint + delta));

    /// <summary>
    /// A non-decreasing sequence of <see cref="GlobalPosition"/> to save into an
    /// <see cref="ICheckpointSink"/>: increments in <c>[0, 4]</c> where 0 is a duplicate position (a
    /// re-delivered position after retry) and ≥ 2 is a gap. Always length ≥ 1 and non-decreasing, so
    /// its maximum is its last element.
    /// </summary>
    public static readonly Gen<GlobalPosition[]> NonDecreasingCheckpointSaves =
        from start in Gen.Int[0, 20]
        from increments in Gen.Int[0, 4].Array[1, 15]
        select FoldNonDecreasing(start, increments);

    /// <summary>
    /// A list of 1..5 positive append batches (each 1..4 events) — drives repeated daemon ticks so
    /// the checkpoint's forward-only advance across a series of gap skips can be asserted.
    /// </summary>
    public static readonly Gen<int[]> PositiveAppendBatches = Gen.Int[1, 4].Array[1, 5];

    private static GlobalPosition[] FoldAscending(int start, int[] increments)
    {
        var positions = new GlobalPosition[increments.Length];
        var current = start;
        for (var i = 0; i < increments.Length; i++)
        {
            current += increments[i]; // each ≥ 1 → strictly ascending; ≥ 2 → a gap
            positions[i] = new GlobalPosition(current);
        }

        return positions;
    }

    private static GlobalPosition[] FoldNonDecreasing(int start, int[] increments)
    {
        var positions = new GlobalPosition[increments.Length];
        var current = start;
        for (var i = 0; i < increments.Length; i++)
        {
            current += increments[i]; // 0 → duplicate position (retry); ≥ 2 → a gap; never decreases
            positions[i] = new GlobalPosition(current);
        }

        return positions;
    }
}
