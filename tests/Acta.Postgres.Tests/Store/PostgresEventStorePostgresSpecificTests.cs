using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.Migrations;
using Acta.Postgres.Store;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.TestSupport;

using Xunit;

namespace Acta.Postgres.Tests.Store;

/// <summary>
/// Postgres-specific facts closing the 2.2/2.3 deferrals against the real backend (task 7.2):
/// ratification of whole-batch dedup (GAP-1), characterization of <c>EmptyStream ≡ NoStream</c> on a
/// new stream (GAP-2), the no-IDENTITY-gap guarantee of the explicit-dedup path, and the ADR-003
/// Enforcement MUST — a multi-pod (two store instances / separate pooled connections) idempotent
/// retry (D3).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresEventStorePostgresSpecificTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- GAP-1: a partially-overlapping batch dedups as a whole (no new rows appended) ----

    [Fact]
    public async Task PartiallyOverlappingBatch_DedupsWholeBatch_AppendsNothing()
    {
        var store = await CreateStoreAsync();
        var existing = TestEvents.OrderPlaced();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, [existing], Ct); // version 0

        var fresh = TestEvents.OrderPlaced();
        // [already-seen, genuinely-new]: any already-seen key dedups the WHOLE batch — the new event
        // must NOT be appended (parity with InMemoryEventStore; differs from per-row ON CONFLICT).
        var result = await store.AppendAsync("order-1", ExpectedVersion.Any, [existing, fresh], Ct);

        result.Deduplicated.Should().BeTrue();
        var stored = await ToListAsync(store.ReadStreamAsync("order-1", ct: Ct));
        stored.Should().ContainSingle();
        stored[0].EventId.Should().Be(existing.EventId);
    }

    // ---- GAP-2: EmptyStream on a brand-new stream behaves exactly like NoStream ----

    [Fact]
    public async Task EmptyStream_OnNewStream_IsEquivalentToNoStream()
    {
        var store = await CreateStoreAsync();

        // The "existing but empty stream" state is unreachable through the public API on Postgres too
        // (the only path that creates a streams row is an append ending at current_version >= 0), so
        // EmptyStream and NoStream are behaviorally identical: both let the first append succeed.
        var result = await store.AppendAsync("order-1", ExpectedVersion.EmptyStream, TestEvents.Distinct(1), Ct);

        result.NextExpectedVersion.Should().Be(0);
        result.Deduplicated.Should().BeFalse();
    }

    // ---- The explicit-dedup path consumes no global_position IDENTITY (no gaps) ----

    [Fact]
    public async Task DedupPath_ConsumesNoGlobalPosition_LeavesNoGap()
    {
        var store = await CreateStoreAsync();
        await store.AppendAsync("order-1", ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct); // positions 1, 2
        var e = TestEvents.OrderPlaced();
        await store.AppendAsync("order-1", ExpectedVersion.Any, [e], Ct);                          // position 3

        // Replay the duplicate: an ON CONFLICT DO NOTHING would burn an IDENTITY value here.
        var replay = await store.AppendAsync("order-1", ExpectedVersion.Any, [e], Ct);
        replay.Deduplicated.Should().BeTrue();

        // The very next genuine append must land on position 4 — contiguous, proving the dedup path
        // burned nothing.
        var next = await store.AppendAsync("order-2", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);
        next.LastGlobalPosition.Value.Should().Be(4);

        var all = await ToListAsync(store.ReadAllAsync(GlobalPosition.Start, ct: Ct));
        long[] contiguous = [1, 2, 3, 4];
        all.Select(x => x.GlobalPosition.Value).Should().Equal(contiguous);
    }

    // ---- ADR-003 Enforcement MUST (D3): multi-pod idempotent retry across two store instances ----

    [Fact]
    public async Task MultiPod_RetryOfSameEvent_AcrossSeparateStoreInstances_IsIdempotent()
    {
        // Two PostgresEventStore instances over the same schema stand in for two pods: each append
        // opens its own pooled connection, so the retry travels a different physical connection.
        var options = new ActaPostgresOptions { SchemaName = PostgresFixture.NewSchemaName() };
        await new MigrationRunner(_fixture.DataSource, options).MigrateAsync(Ct);
        var podA = new PostgresEventStore(_fixture.DataSource, options);
        var podB = new PostgresEventStore(_fixture.DataSource, options);

        var e = TestEvents.OrderPlaced();
        var first = await podA.AppendAsync("order-1", ExpectedVersion.NoStream, [e], Ct);
        first.Deduplicated.Should().BeFalse();
        first.NextExpectedVersion.Should().Be(0);

        // Pod B replays the same command with its original (now-stale) NoStream guard: dedup-before-
        // guard recognizes the committed event through the shared database and returns an idempotent
        // success — it must NOT throw ConcurrencyException, and it reports the unchanged head.
        var retry = await podB.AppendAsync("order-1", ExpectedVersion.NoStream, [e], Ct);
        retry.Deduplicated.Should().BeTrue();
        retry.NextExpectedVersion.Should().Be(0);
    }

    private async ValueTask<IEventStore> CreateStoreAsync()
    {
        var options = new ActaPostgresOptions { SchemaName = PostgresFixture.NewSchemaName() };
        await new MigrationRunner(_fixture.DataSource, options).MigrateAsync(Ct);
        return new PostgresEventStore(_fixture.DataSource, options);
    }

    private static async Task<List<StoredEvent>> ToListAsync(IAsyncEnumerable<StoredEvent> source)
    {
        var result = new List<StoredEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }
}
