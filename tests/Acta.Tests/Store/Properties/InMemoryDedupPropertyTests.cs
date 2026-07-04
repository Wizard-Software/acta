using CsCheck;
using Xunit;

using Acta.Abstractions;
using Acta.InMemory;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Store.Properties;

/// <summary>
/// Property tests for the "Dedup (ADR-003)" row of the TESTING-SPEC §6.1 edge-case catalogue
/// (task 2.3 — NR3 part 1), exercised against <see cref="InMemoryEventStore"/> via CsCheck.
/// <para>
/// The four edges map to ADR-003 with two levels of strength (plan §2.4):
/// edge (b) "same EventId across two batches" and edge (c) "under different ExpectedVersion" assert
/// <b>binding</b> ADR-003 invariants (idempotent replay; dedup precedes the concurrency guard) that
/// every backend must satisfy; edge (a-ii) "same EventId within one batch" and edge (d) "retry after
/// partial success" (GAP-1) are <b>characterizations</b> of the current in-memory default — pinned
/// here against regression, with cross-backend ratification (which may force a src/ change or an
/// ADR-003 addendum) deferred to the Postgres backend in task 7.2.
/// </para>
/// <para>
/// Each property builds its OWN <see cref="InMemoryEventStore"/> per iteration — CsCheck runs samples
/// in parallel, so a shared store would race and leak state across iterations. The
/// <see cref="CancellationToken"/> is captured <i>before</i> <c>SampleAsync</c>: xUnit's
/// <see cref="TestContext.Current"/> is an <c>AsyncLocal</c> and does not flow into CsCheck's worker
/// threads (analyzer xUnit1051 requires the token be forwarded to <c>AppendAsync</c>).
/// </para>
/// </summary>
public sealed class InMemoryDedupPropertyTests
{
    private const string Stream = "order-1";

    // ---- Edge (b): same EventId in two batches — BINDING (ADR-003 idempotent replay) ----

    [Fact]
    public async Task Property_ReplayOfSameBatchAcrossAppends_IsIdempotentSuccess()
    {
        var ct = TestContext.Current.CancellationToken;

        await DedupGenerators.DistinctBatch.SampleAsync(async batch =>
        {
            var store = new InMemoryEventStore();

            var first = await store.AppendAsync(Stream, ExpectedVersion.Any, batch, ct);
            var replay = await store.AppendAsync(Stream, ExpectedVersion.Any, batch, ct);

            first.Deduplicated.Should().BeFalse();
            replay.Deduplicated.Should().BeTrue();
            replay.NextExpectedVersion.Should().Be(first.NextExpectedVersion);
            (await StreamCountAsync(store, ct)).Should().Be(batch.Length);
        });
    }

    // ---- Edge (c): under different ExpectedVersion — BINDING (ADR-003 D3, dedup precedes guard) ----

    [Fact]
    public async Task Property_FullReplay_DedupsBeforeGuard_ForEveryExpectedVersionMode()
    {
        var ct = TestContext.Current.CancellationToken;

        var gen =
            from batch in DedupGenerators.DistinctBatch
            from mode in DedupGenerators.ExpectedVersionMode
            select (batch, mode);

        await gen.SampleAsync(async input =>
        {
            var (batch, mode) = input;
            var store = new InMemoryEventStore();
            await store.AppendAsync(Stream, ExpectedVersion.Any, batch, ct); // stream now exists

            // A full replay is recognized as a duplicate BEFORE the concurrency guard, so it never
            // throws — even under a mode whose guard would otherwise fail (NoStream on an existing
            // stream; the always-mismatching exact version 999).
            var replay = await store.AppendAsync(Stream, mode, batch, ct);

            replay.Deduplicated.Should().BeTrue();
            (await StreamCountAsync(store, ct)).Should().Be(batch.Length);
        });
    }

    // ---- Edge (a): same EventId within one batch ----

    [Fact]
    public async Task Property_ReplayOfBatchWithIntraDuplicate_IsIdempotent()
    {
        // BINDING: once persisted, a replay of the batch (duplicate included) is an idempotent no-op.
        var ct = TestContext.Current.CancellationToken;

        await DedupGenerators.BatchWithIntraDuplicate.SampleAsync(async batch =>
        {
            var store = new InMemoryEventStore();
            await store.AppendAsync(Stream, ExpectedVersion.Any, batch, ct);
            var countAfterFirst = await StreamCountAsync(store, ct);

            var replay = await store.AppendAsync(Stream, ExpectedVersion.Any, batch, ct);

            replay.Deduplicated.Should().BeTrue();
            (await StreamCountAsync(store, ct)).Should().Be(countAfterFirst);
        });
    }

    [Fact]
    public async Task Property_FirstAppendOfIntraDuplicateBatch_InMemoryDefault_AppendsEveryElement()
    {
        // CHARACTERIZATION (in-memory default — pending 7.2, Open Question #2): the in-memory backend
        // does NOT collapse an intra-batch duplicate on first append. HasAnyDuplicateKey checks only
        // the PERSISTED dedup set, so both copies of a repeated EventId are stored. Postgres'
        // UNIQUE(stream_id, event_id) would collapse it — cross-backend ratification is deferred to 7.2.
        var ct = TestContext.Current.CancellationToken;

        await DedupGenerators.BatchWithIntraDuplicate.SampleAsync(async batch =>
        {
            var store = new InMemoryEventStore();

            var result = await store.AppendAsync(Stream, ExpectedVersion.NoStream, batch, ct);

            result.Deduplicated.Should().BeFalse();
            (await StreamCountAsync(store, ct)).Should().Be(batch.Length); // both copies stored
        });
    }

    // ---- Edge (d): retry after partial success (GAP-1 partial-overlap) — CHARACTERIZATION ----

    [Fact]
    public async Task Property_PartiallyOverlappingRetry_InMemoryDefault_DedupsWholeBatch()
    {
        // CHARACTERIZATION (in-memory default / GAP-1 — pending 7.2, Open Question #1): any already-seen
        // key makes the WHOLE retry batch a duplicate — none of its genuinely-fresh events are appended.
        // Postgres' per-row ON CONFLICT DO NOTHING would append the fresh rows; ratification deferred to 7.2.
        var ct = TestContext.Current.CancellationToken;

        await DedupGenerators.PartiallyOverlappingBatches.SampleAsync(async pair =>
        {
            var (first, partialRetry) = pair;
            var store = new InMemoryEventStore();
            await store.AppendAsync(Stream, ExpectedVersion.Any, first, ct);

            var retry = await store.AppendAsync(Stream, ExpectedVersion.Any, partialRetry, ct);

            retry.Deduplicated.Should().BeTrue();
            (await StreamCountAsync(store, ct)).Should().Be(first.Length); // no fresh event appended

            // No fresh key leaked into the stream: every stored EventId belongs to the original batch.
            var firstIds = first.Select(e => e.EventId).ToHashSet();
            await foreach (var stored in store.ReadStreamAsync(Stream, ct: ct))
            {
                firstIds.Should().Contain(stored.EventId);
            }
        });
    }

    private static async Task<int> StreamCountAsync(IEventStore store, CancellationToken ct)
    {
        var count = 0;
        await foreach (var _ in store.ReadStreamAsync(Stream, ct: ct))
        {
            count++;
        }

        return count;
    }
}
