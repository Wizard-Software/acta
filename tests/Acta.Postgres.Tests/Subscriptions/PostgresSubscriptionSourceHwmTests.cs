using System.Diagnostics.CodeAnalysis;

using Acta.Abstractions;
using Acta.Postgres.Store;
using Acta.Postgres.Subscriptions;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Npgsql;

using Xunit;

namespace Acta.Postgres.Tests.Subscriptions;

/// <summary>
/// Postgres-specific safe-HWM facts for <see cref="PostgresSubscriptionSource"/> (task 7.3, P1) that
/// have no in-memory analogue: the visibility-lag cutback protecting against the concurrent-gap-at-
/// end-of-batch loss (the P1 closing criterion), withholding of fresh events, and event-type pushdown
/// verified against the real backend. A <see cref="FakeTimeProvider"/> drives both the store's
/// <c>created_at</c> stamp and the source's cutback so the scenarios are fully deterministic (no
/// wall-clock sleeps).
/// </summary>
[Collection(PostgresCollection.Name)]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "Schema names come from PostgresFixture.NewSchemaName (allow-list-valid, " +
        "test-controlled); the helper interpolates only that identifier to seed an uncommitted row.")]
public sealed class PostgresSubscriptionSourceHwmTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// P1 closing criterion: with a concurrent, still-in-flight append holding the next sequence
    /// value uncommitted, a fresh committed event sitting AFTER that gap must NOT be returned — the
    /// daemon cannot leap the gap and advance its checkpoint past the still-uncommitted position
    /// (which would lose it on commit). After the in-flight transaction rolls the gap into a permanent
    /// one and the tail ages past the cutback, the daemon advances over it.
    /// </summary>
    [Fact]
    public async Task ConcurrentGapAtEndOfBatch_DoesNotLeapUncommittedPosition()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var clock = new FakeTimeProvider();
        var visibilityLag = TimeSpan.FromSeconds(30);
        var store = new PostgresEventStore(_fixture.DataSource, options, clock);
        var source = new PostgresSubscriptionSource(_fixture.DataSource, options, visibilityLag, clock);

        // Two safely-committed events at T0 (global positions 1, 2).
        await store.AppendAsync("a-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);
        await store.AppendAsync("b-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);

        // Age them past the cutback window so they are safe to read (cutoff = now - 30s = T0 + 1s).
        clock.Advance(visibilityLag + TimeSpan.FromSeconds(1)); // now T0 + 31s

        // Simulate a concurrent in-flight append holding the NEXT sequence value (gp 3) uncommitted.
        await using var gapConnection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var gapTx = await gapConnection.BeginTransactionAsync(Ct);
        await InsertUncommittedEventAsync(gapConnection, gapTx, options.SchemaName, "c-open", clock.GetUtcNow(), Ct);

        // A fresh committed event AFTER the gap (gp 4) — younger than the cutback window.
        await store.AppendAsync("d-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);

        // The daemon must NOT leap the in-flight gp 3 to grab the fresh gp 4: gp 4 is withheld
        // (younger than the cutback), gp 3 is invisible (uncommitted). Only the aged 1, 2 are safe.
        var safeBatch = await source.ReadBatchAsync(GlobalPosition.Start, maxCount: 10, ct: Ct);
        safeBatch.Select(e => e.GlobalPosition.Value).Should().Equal(1L, 2L);

        // Roll the in-flight transaction back => gp 3 becomes a PERMANENT gap.
        await gapTx.RollbackAsync(Ct);

        // Age gp 4 past the cutback; the daemon now advances over the permanent gap 3 to gp 4.
        clock.Advance(visibilityLag + TimeSpan.FromSeconds(1)); // now T0 + 62s, cutoff = T0 + 32s
        var afterBatch = await source.ReadBatchAsync(new GlobalPosition(2), maxCount: 10, ct: Ct);
        afterBatch.Select(e => e.GlobalPosition.Value).Should().Equal(4L);
    }

    /// <summary>A freshly committed event is withheld while inside the visibility-lag window and
    /// becomes visible only once it ages past the cutback.</summary>
    [Fact]
    public async Task FreshEvent_WithinVisibilityLag_IsWithheldUntilAged()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var clock = new FakeTimeProvider();
        var visibilityLag = TimeSpan.FromSeconds(30);
        var store = new PostgresEventStore(_fixture.DataSource, options, clock);
        var source = new PostgresSubscriptionSource(_fixture.DataSource, options, visibilityLag, clock);

        await store.AppendAsync("a-1", ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);

        var withheld = await source.ReadBatchAsync(GlobalPosition.Start, maxCount: 10, ct: Ct);
        withheld.Should().BeEmpty();

        clock.Advance(visibilityLag + TimeSpan.FromSeconds(1));
        var aged = await source.ReadBatchAsync(GlobalPosition.Start, maxCount: 10, ct: Ct);
        aged.Select(e => e.GlobalPosition.Value).Should().Equal(1L);
    }

    /// <summary>The event-type filter is pushed down to SQL with <c>LIMIT</c> applied AFTER the
    /// filter, so <c>maxCount</c> counts matching events (verified against the real backend).</summary>
    [Fact]
    public async Task EventTypesFilter_IsPushedDownWithLimitCountingMatches()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var clock = new FakeTimeProvider();
        var store = new PostgresEventStore(_fixture.DataSource, options, clock);
        var source = new PostgresSubscriptionSource(_fixture.DataSource, options, TimeSpan.Zero, clock);

        // Interleaved types: OrderPlaced at gp 1, 3, 5; OrderCancelled at 2, 4, 6.
        await store.AppendAsync(
            "order-1",
            ExpectedVersion.NoStream,
            [
                TestEvents.OrderPlaced(), TestEvents.OrderCancelled(),
                TestEvents.OrderPlaced(), TestEvents.OrderCancelled(),
                TestEvents.OrderPlaced(), TestEvents.OrderCancelled(),
            ],
            Ct);

        // Zero lag + one tick so created_at < cutoff (strict predicate).
        clock.Advance(TimeSpan.FromSeconds(1));

        var batch = await source.ReadBatchAsync(
            GlobalPosition.Start,
            maxCount: 2,
            eventTypes: new HashSet<string> { "OrderPlaced" },
            ct: Ct);

        // LIMIT applied AFTER the pushed-down type filter: OrderPlaced at gp 1 and 3 (scanning past
        // the interleaved OrderCancelled at gp 2), NOT gp 1 alone.
        batch.Select(e => e.GlobalPosition.Value).Should().Equal(1L, 3L);
        batch.Should().OnlyContain(e => e.EventType == "OrderPlaced");
    }

    // Seeds a stream row + one event inside the caller's still-open transaction, allocating (but not
    // committing) the next global_position IDENTITY value — the in-flight gap the guard must respect.
    private static async Task InsertUncommittedEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string streamId,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        await using var streamCommand = new NpgsqlCommand(
            $"INSERT INTO {schema}.streams (stream_id, category, tenant_id, current_version) VALUES (@sid, @sid, NULL, 0)",
            connection, transaction);
        streamCommand.Parameters.AddWithValue("sid", streamId);
        await streamCommand.ExecuteNonQueryAsync(ct);

        await using var eventCommand = new NpgsqlCommand(
            $$"""
             INSERT INTO {{schema}}.events
                 (stream_id, version, event_id, event_type, schema_version, payload, metadata, tenant_id, created_at)
             VALUES (@sid, 0, @eid, 'OrderPlaced', 1, '[1,2,3]'::jsonb, '{}'::jsonb, NULL, @created)
             """,
            connection, transaction);
        eventCommand.Parameters.AddWithValue("sid", streamId);
        eventCommand.Parameters.AddWithValue("eid", Guid.NewGuid());
        eventCommand.Parameters.AddWithValue("created", createdAt);
        await eventCommand.ExecuteNonQueryAsync(ct);
    }
}
