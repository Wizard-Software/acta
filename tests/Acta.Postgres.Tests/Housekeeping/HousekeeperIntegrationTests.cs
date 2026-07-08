using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

using Acta.Postgres.Configuration;
using Acta.Postgres.Housekeeping;
using Acta.Postgres.Tests.Infrastructure;

using Npgsql;

using NpgsqlTypes;

using Xunit;

namespace Acta.Postgres.Tests.Housekeeping;

/// <summary>
/// Postgres-specific housekeeping facts closing R3-B5 against the real backend (04-data §3.6): the
/// single-active sweep purges published outbox rows past retention, expired idempotency and reservation
/// entries (per-tenant isolation — live entries of other tenants survive), and aged dead-letter rows;
/// it skips when another pod holds the <c>{schema}:housekeeping</c> advisory lock; a
/// <see cref="TimeSpan.Zero"/> retention disables that table's purge; and it emits
/// <c>acta.housekeeping.purged</c>. Timestamps are seeded relative to the server clock (<c>now()</c>)
/// with explicit intervals, so every expiry boundary is reached deterministically without a wall-clock
/// wait.
/// </summary>
[Collection(PostgresCollection.Name)]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The seed/count/lock helpers interpolate only the schema name produced by " +
        "PostgresFixture.NewSchemaName() (an allow-list-valid acta_test_<guid>), never external input; " +
        "every value travels as an NpgsqlParameter. Same sanctioned identifier-interpolation exception " +
        "as the production Housekeeper.")]
public sealed class HousekeeperIntegrationTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OutboxPurge_DeletesPublishedPastRetention_KeepsRecentAndUnpublished()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var schema = options.SchemaName;

        await SeedOutboxAsync(schema, publishedAgo: TimeSpan.FromDays(10)); // past 7-day retention → purged
        await SeedOutboxAsync(schema, publishedAgo: TimeSpan.FromDays(1));  // within retention → survives
        await SeedOutboxAsync(schema, publishedAgo: null);                  // unpublished → survives

        var report = await new Housekeeper(_fixture.DataSource, options).SweepAsync(Ct);

        report.Executed.Should().BeTrue();
        report.OutboxPurged.Should().Be(1);
        (await CountAsync(schema, "outbox")).Should().Be(2);
    }

    [Fact]
    public async Task IdempotencyPurge_PerTenant_DeletesExpired_KeepsLiveOfOtherTenants()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var schema = options.SchemaName;

        // Same command key across two tenants: only the expired entry is purged, the live one survives —
        // per-tenant isolation (§6.1 idempotency, R3/ADR-016).
        await SeedIdempotencyAsync(schema, tenant: "tenant-a", key: "cmd-1", expiresIn: TimeSpan.FromMinutes(-1)); // expired → purged
        await SeedIdempotencyAsync(schema, tenant: "tenant-b", key: "cmd-1", expiresIn: TimeSpan.FromMinutes(5));  // live → survives
        await SeedIdempotencyAsync(schema, tenant: "tenant-a", key: "cmd-2", expiresIn: TimeSpan.FromMinutes(5));  // live → survives

        var report = await new Housekeeper(_fixture.DataSource, options).SweepAsync(Ct);

        report.IdempotencyPurged.Should().Be(1);
        (await CountAsync(schema, "idempotency")).Should().Be(2);
    }

    [Fact]
    public async Task ReservationsPurge_PerTenant_DeletesExpiredUnconfirmed_KeepsConfirmedAndLive()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var schema = options.SchemaName;

        // Expired + unconfirmed past the 1h sweep grace → purged.
        await SeedReservationAsync(schema, tenant: "tenant-a", value: "x@acta.io", confirmed: false, expiresAgo: TimeSpan.FromHours(2));
        // Same value, different tenant, still live → survives (per-tenant isolation, §6.1 reservations).
        await SeedReservationAsync(schema, tenant: "tenant-b", value: "x@acta.io", confirmed: false, expiresIn: TimeSpan.FromMinutes(30));
        // Confirmed (expires_at NULL) → never swept.
        await SeedReservationAsync(schema, tenant: "tenant-a", value: "y@acta.io", confirmed: true, expiresAgo: null);

        var report = await new Housekeeper(_fixture.DataSource, options).SweepAsync(Ct);

        report.ReservationsPurged.Should().Be(1);
        (await CountAsync(schema, "reservations")).Should().Be(2);
    }

    [Fact]
    public async Task DeadLetterPurge_DeletesPastRetention_KeepsRecent()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var schema = options.SchemaName;

        await SeedDeadLetterAsync(schema, failedAgo: TimeSpan.FromDays(40)); // past 30-day retention → purged
        await SeedDeadLetterAsync(schema, failedAgo: TimeSpan.FromDays(1));  // within retention → survives

        var report = await new Housekeeper(_fixture.DataSource, options).SweepAsync(Ct);

        report.DeadLetterPurged.Should().Be(1);
        (await CountAsync(schema, "projection_dead_letter")).Should().Be(1);
    }

    [Fact]
    public async Task SingleActive_WhenLockHeldByAnotherPod_SweepSkipsAndPurgesNothing()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var schema = options.SchemaName;
        await SeedOutboxAsync(schema, publishedAgo: TimeSpan.FromDays(10)); // would be purged if the sweep ran

        var housekeeper = new Housekeeper(_fixture.DataSource, options);

        // Another pod holds {schema}:housekeeping on a dedicated session.
        await using (var holder = await _fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await AcquireHousekeepingLockAsync(holder, schema);

            var skipped = await housekeeper.SweepAsync(Ct);

            skipped.Executed.Should().BeFalse();
            skipped.TotalPurged.Should().Be(0);
            (await CountAsync(schema, "outbox")).Should().Be(1); // untouched — the sweep was skipped

            // Release explicitly (deterministic) — a pooled connection is not guaranteed to drop a
            // session-level advisory lock the instant it is returned to the pool.
            await ReleaseHousekeepingLockAsync(holder, schema);
        }

        // Lock released → the next sweep runs and purges.
        var ran = await housekeeper.SweepAsync(Ct);
        ran.Executed.Should().BeTrue();
        ran.OutboxPurged.Should().Be(1);
        (await CountAsync(schema, "outbox")).Should().Be(0);
    }

    [Fact]
    public async Task DisabledRetention_ZeroOutboxRetention_SkipsOutboxPurge()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        options.Housekeeping.PublishedOutboxRetention = TimeSpan.Zero; // explicit host decision: disable
        var schema = options.SchemaName;

        await SeedOutboxAsync(schema, publishedAgo: TimeSpan.FromDays(365)); // ancient, but purge is disabled

        var report = await new Housekeeper(_fixture.DataSource, options).SweepAsync(Ct);

        report.Executed.Should().BeTrue();
        report.OutboxPurged.Should().Be(0);
        (await CountAsync(schema, "outbox")).Should().Be(1);
    }

    [Fact]
    public async Task Sweep_EmitsPurgedMetric_TaggedByTable()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var schema = options.SchemaName;
        await SeedOutboxAsync(schema, publishedAgo: TimeSpan.FromDays(10));

        using var metrics = new HousekeepingMetrics(); // privately-owned Meter("Acta")
        var recorded = new List<(string Table, long Value)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Acta" && instrument.Name == "acta.housekeeping.purged")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? table = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "table")
                {
                    table = tag.Value as string;
                }
            }

            lock (recorded)
            {
                recorded.Add((table!, value));
            }
        });
        listener.Start();

        await new Housekeeper(_fixture.DataSource, options, metrics).SweepAsync(Ct);
        listener.Dispose();

        recorded.Should().ContainSingle(m => m.Table == "outbox" && m.Value == 1);
    }

    // ---- seed / count / lock helpers ----

    private async Task SeedOutboxAsync(string schema, TimeSpan? publishedAgo)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        var publishedExpr = publishedAgo is null ? "NULL" : "now() - @publishedAgo";
        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO {schema}.outbox (message_id, event_type, payload, metadata, published_at)
             VALUES (@mid, 'Test.Event', @payload, @metadata, {publishedExpr})
             """,
            connection);
        command.Parameters.AddWithValue("mid", Guid.NewGuid());
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = "{}" });
        command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = "{}" });
        if (publishedAgo is { } ago)
        {
            command.Parameters.Add(new NpgsqlParameter("publishedAgo", NpgsqlDbType.Interval) { Value = ago });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task SeedIdempotencyAsync(string schema, string tenant, string key, TimeSpan expiresIn)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO {schema}.idempotency (tenant_id, idempotency_key, result, expires_at)
             VALUES (@tenant, @key, NULL, now() + @expiresIn)
             """,
            connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.Add(new NpgsqlParameter("expiresIn", NpgsqlDbType.Interval) { Value = expiresIn });

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task SeedReservationAsync(
        string schema, string tenant, string value, bool confirmed,
        TimeSpan? expiresAgo = null, TimeSpan? expiresIn = null)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        var expiresExpr = confirmed
            ? "NULL"
            : expiresAgo is not null ? "now() - @offset" : "now() + @offset";
        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO {schema}.reservations (tenant_id, scope, value, owner_id, expires_at, confirmed)
             VALUES (@tenant, 'email', @value, 'owner', {expiresExpr}, @confirmed)
             """,
            connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("confirmed", confirmed);
        var offset = expiresAgo ?? expiresIn;
        if (!confirmed && offset is { } o)
        {
            command.Parameters.Add(new NpgsqlParameter("offset", NpgsqlDbType.Interval) { Value = o });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task SeedDeadLetterAsync(string schema, TimeSpan failedAgo)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO {schema}.projection_dead_letter
                    (projection_name, tenant_id, global_position, event_id, error, attempts, first_failed_at)
             VALUES ('proj', '', 1, @eid, 'boom', 3, now() - @failedAgo)
             """,
            connection);
        command.Parameters.AddWithValue("eid", Guid.NewGuid());
        command.Parameters.Add(new NpgsqlParameter("failedAgo", NpgsqlDbType.Interval) { Value = failedAgo });

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> CountAsync(string schema, string table)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {schema}.{table}", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task AcquireHousekeepingLockAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtextextended(@key, 0))", connection);
        command.Parameters.AddWithValue("key", $"{schema}:housekeeping");
        await command.ExecuteScalarAsync(Ct);
    }

    private async Task ReleaseHousekeepingLockAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_unlock(hashtextextended(@key, 0))", connection);
        command.Parameters.AddWithValue("key", $"{schema}:housekeeping");
        await command.ExecuteScalarAsync(Ct);
    }
}
