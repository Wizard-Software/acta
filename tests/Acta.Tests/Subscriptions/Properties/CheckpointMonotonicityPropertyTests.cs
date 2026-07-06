using CsCheck;
using Xunit;

using Acta.Abstractions;
using Acta.InMemory;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Subscriptions.Properties;

/// <summary>
/// Property tests for the checkpoint-monotonicity side of the "Kolejność / monotoniczność" row of
/// TESTING-SPEC §6.1 (task 5.5 — NR3 part 2), exercised against <see cref="InMemoryCheckpointSink"/>
/// (the <see cref="ICheckpointSink"/> contract, task 5.1) over generated non-decreasing
/// <see cref="GlobalPosition"/> save sequences with gaps and duplicates.
/// <para>
/// The row's binding invariants (ADR-005): a checkpoint only ever advances; saving the SAME position
/// again — a re-delivered position after a retry — is an idempotent no-op; and any save that would move
/// the checkpoint backward throws (there is no rollback outside an explicit rebuild). An explicit
/// rebuild is NOT a <see cref="ICheckpointSink.SaveAsync"/> operation — it is an out-of-band reset (the
/// Postgres sink's concern, tasks 7.5/7.6) — so at the sink surface every backward save is forbidden,
/// unconditionally.
/// </para>
/// <para>
/// Each property builds its OWN <see cref="InMemoryCheckpointSink"/> per iteration — CsCheck runs
/// samples in parallel, so a shared sink would race and leak state. The <see cref="CancellationToken"/>
/// is captured BEFORE <c>SampleAsync</c>: xUnit's <see cref="TestContext.Current"/> is an
/// <c>AsyncLocal</c> that does not flow into CsCheck's worker threads (analyzer xUnit1051), mirroring
/// <c>InMemoryDedupPropertyTests</c>.
/// </para>
/// </summary>
public sealed class CheckpointMonotonicityPropertyTests
{
    private const string Proj = "checkpoint-monotonicity-prop";
    private const string Owner = "owner-token";

    // ---- C1: a non-decreasing sequence of saves (gaps + equal steps) leaves the checkpoint at its max ----

    [Fact]
    public async Task Property_MonotonicSaves_LoadReturnsMax()
    {
        var ct = TestContext.Current.CancellationToken;

        await GlobalPositionSequenceGenerators.NonDecreasingCheckpointSaves.SampleAsync(async sequence =>
        {
            var sink = new InMemoryCheckpointSink();

            foreach (var position in sequence)
            {
                await sink.SaveAsync(Proj, tenantId: null, position, Owner, ct); // never below current → never throws
            }

            // The sequence is non-decreasing, so its maximum is its last element.
            (await sink.LoadAsync(Proj, tenantId: null, ct)).Should().Be(sequence[^1]);
        });
    }

    // ---- C2: a duplicate position re-delivered after a retry is an idempotent no-op ----

    [Fact]
    public async Task Property_DuplicatePositionAfterRetry_IsIdempotentNoOp()
    {
        var ct = TestContext.Current.CancellationToken;

        var gen =
            from value in Gen.Int[0, 200]
            from repeats in Gen.Int[2, 6]
            select (value, repeats);

        await gen.SampleAsync(async input =>
        {
            var (value, repeats) = input;
            var sink = new InMemoryCheckpointSink();
            var position = new GlobalPosition(value);

            for (var i = 0; i < repeats; i++)
            {
                await sink.SaveAsync(Proj, tenantId: null, position, Owner, ct); // retry re-delivers the same position
            }

            // Equal saves neither throw nor move the checkpoint.
            (await sink.LoadAsync(Proj, tenantId: null, ct)).Should().Be(position);
        });
    }

    // ---- C3: any backward save throws and leaves the checkpoint unchanged ("checkpoint nigdy wstecz") ----

    [Fact]
    public async Task Property_AnyBackwardSave_ThrowsAndCheckpointUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;

        var gen =
            from sequence in GlobalPositionSequenceGenerators.NonDecreasingCheckpointSaves
            from back in Gen.Int[1, 30]
            select (sequence, back);

        await gen.SampleAsync(async input =>
        {
            var (sequence, back) = input;
            var sink = new InMemoryCheckpointSink();

            foreach (var position in sequence)
            {
                await sink.SaveAsync(Proj, tenantId: null, position, Owner, ct);
            }

            var max = sequence[^1];
            var backward = new GlobalPosition(max.Value - back); // strictly below the established max

            await Awaiting(() => sink.SaveAsync(Proj, tenantId: null, backward, Owner, ct).AsTask())
                .Should().ThrowAsync<InvalidOperationException>();

            (await sink.LoadAsync(Proj, tenantId: null, ct)).Should().Be(max); // rejected save changed nothing
        });
    }

    // ---- C4: per-step retry (save then immediately re-save the same position) is idempotent throughout ----

    [Fact]
    public async Task Property_PerStepRetryOfNonDecreasingSequence_FinalIsMax()
    {
        var ct = TestContext.Current.CancellationToken;

        await GlobalPositionSequenceGenerators.NonDecreasingCheckpointSaves.SampleAsync(async sequence =>
        {
            var sink = new InMemoryCheckpointSink();

            foreach (var position in sequence)
            {
                await sink.SaveAsync(Proj, tenantId: null, position, Owner, ct);
                await sink.SaveAsync(Proj, tenantId: null, position, Owner, ct); // immediate retry — idempotent no-op
                (await sink.LoadAsync(Proj, tenantId: null, ct)).Should().Be(position);
            }

            (await sink.LoadAsync(Proj, tenantId: null, ct)).Should().Be(sequence[^1]);
        });
    }
}
