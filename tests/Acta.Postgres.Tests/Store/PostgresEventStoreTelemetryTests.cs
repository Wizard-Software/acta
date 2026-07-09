using Acta.Abstractions;
using Acta.Diagnostics;
using Acta.Postgres.Configuration;
using Acta.Postgres.Migrations;
using Acta.Postgres.Store;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.TestSupport;

using Xunit;

namespace Acta.Postgres.Tests.Store;

/// <summary>
/// Integration coverage for AK-5 (task 8.6, 06-cross-cutting.md §2) on the real PostgreSQL backend:
/// the same <c>acta.append</c>/<c>acta.read</c> spans and <c>acta.append.throughput</c> counter proven
/// against <see cref="Acta.InMemory.InMemoryEventStore"/> in <c>EventStoreTelemetryTests</c>, tagged
/// <c>acta.backend = "postgres"</c> — the "on both backends" half of AK-5's verification. Each test
/// gets a fresh, migrated schema in the shared Testcontainers container (isolation by schema name, not
/// by container), mirroring <see cref="PostgresEventStoreContractTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresEventStoreTelemetryTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Append_emits_acta_append_span_with_stream_and_count_tags()
    {
        var store = await CreateStoreAsync();
        var streamId = $"stream-{Guid.NewGuid():N}";
        var events = TestEvents.Distinct(3);

        using var spans = new ActivitySpanCollector();

        await store.AppendAsync(streamId, ExpectedVersion.NoStream, events, Ct);

        var span = spans.FindSpan(ActaDiagnostics.AppendSpan);
        span.Should().NotBeNull();
        ((string?)span!.GetTagItem(ActaDiagnostics.StreamIdTag)).Should().Be(streamId);
        ((int)span.GetTagItem(ActaDiagnostics.EventCountTag)!).Should().Be(events.Length);
        ((string?)span.GetTagItem(ActaDiagnostics.BackendTag)).Should().Be("postgres");
    }

    [Fact]
    public async Task Read_emits_acta_read_span_tagged_postgres()
    {
        var store = await CreateStoreAsync();
        var streamId = $"stream-{Guid.NewGuid():N}";
        await store.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);

        using var spans = new ActivitySpanCollector();

        var read = new List<StoredEvent>();
        await foreach (var storedEvent in store.ReadStreamAsync(streamId, ct: Ct))
        {
            read.Add(storedEvent);
        }

        var span = spans.FindSpan(ActaDiagnostics.ReadSpan);
        span.Should().NotBeNull();
        read.Should().HaveCount(2);
        ((string?)span!.GetTagItem(ActaDiagnostics.StreamIdTag)).Should().Be(streamId);
        ((string?)span.GetTagItem(ActaDiagnostics.BackendTag)).Should().Be("postgres");
        ((int)span.GetTagItem(ActaDiagnostics.EventCountTag)!).Should().Be(2);
    }

    [Fact]
    public async Task Append_records_append_throughput_counter_tagged_postgres()
    {
        using var observer = new AppendThroughputObserver("postgres");
        using var metrics = new EventStoreMetrics();
        var options = new ActaPostgresOptions { SchemaName = PostgresFixture.NewSchemaName() };
        await new MigrationRunner(_fixture.DataSource, options).MigrateAsync(Ct);
        var store = new PostgresEventStore(_fixture.DataSource, options, metrics: metrics);
        var events = TestEvents.Distinct(4);

        await store.AppendAsync($"stream-{Guid.NewGuid():N}", ExpectedVersion.NoStream, events, Ct);

        observer.Count.Should().Be(events.Length);
    }

    private async ValueTask<PostgresEventStore> CreateStoreAsync()
    {
        var options = new ActaPostgresOptions { SchemaName = PostgresFixture.NewSchemaName() };
        await new MigrationRunner(_fixture.DataSource, options).MigrateAsync(Ct);
        return new PostgresEventStore(_fixture.DataSource, options);
    }
}
