using System.Diagnostics.CodeAnalysis;

using Npgsql;

using Xunit;

using Acta.Abstractions;
using Acta.InMemory;
using Acta.Postgres.Configuration;
using Acta.Postgres.Store;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.TestSupport;

namespace Acta.Postgres.Tests.Outbox;

/// <summary>
/// AK-1 (ADR-002, FR-14), task 8.4: proves the single-commit outbox seam on the real PostgreSQL
/// backend — domain append and outbox enlistment through the same
/// <see cref="PostgresEventAppendTransaction"/> become visible atomically (one all-or-nothing
/// commit), or nothing at all on rollback (dispose without commit). Mirrors
/// <c>Acta.Tests.Outbox.SingleCommitSeamTests</c> (the in-memory proof of the same seam) 1:1, but
/// asserts against real rows in <c>{schema}.events</c>/<c>{schema}.outbox</c> — read directly, bypassing
/// the store — instead of an in-memory buffer. Each test gets a fresh, migrated schema in the shared
/// Testcontainers container (isolation by schema name, not by container), mirroring
/// <see cref="PostgresEventStoreContractTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The read helpers interpolate only the schema name produced by PostgresSchemaSetup." +
        "MigrateFreshSchemaAsync (an allow-list-valid acta_test_<guid>), never external input; every " +
        "runtime value (stream id) travels as an NpgsqlParameter. Same sanctioned identifier-" +
        "interpolation exception as the production store.")]
public sealed class PostgresSingleCommitSeamTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A <see cref="TimeProvider"/> that always reports a fixed, caller-supplied instant.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A stand-in integration event — its CLR <see cref="Type.FullName"/> is what the D3 <c>event_type</c> mapping asserts against.</summary>
    private sealed record OrderPlacedIntegrationEvent(string OrderId);

    [Fact]
    public async Task Commit_MakesAppendAndOutboxVisibleAtomically()
    {
        var (options, factory) = await CreateFactoryAsync();
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new PostgresOutboxFlush(collector);
        var streamId = NewStreamId();
        var messageId = Guid.NewGuid();
        collector.Collect(new OrderPlacedIntegrationEvent("order-1"), CreateMetadata(messageId, tenantId: "tenant-a"));

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);
            await flush.FlushAsync(tx, Ct);
            await tx.CommitAsync(Ct);
        }

        (await CountEventsAsync(options, streamId)).Should().Be(1);
        var outboxRow = await ReadSingleOutboxRowAsync(options);
        outboxRow.MessageId.Should().Be(messageId);
        outboxRow.EventType.Should().Be(typeof(OrderPlacedIntegrationEvent).FullName);
        outboxRow.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task Dispose_WithoutCommit_LeavesNothingVisible()
    {
        var (options, factory) = await CreateFactoryAsync();
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new PostgresOutboxFlush(collector);
        var streamId = NewStreamId();
        collector.Collect(new OrderPlacedIntegrationEvent("order-1"), CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);
            await flush.FlushAsync(tx, Ct);
            // No CommitAsync call — DisposeAsync at the end of this block must roll back.
        }

        (await CountEventsAsync(options, streamId)).Should().Be(0);
        (await CountOutboxRowsAsync(options)).Should().Be(0);
    }

    [Fact]
    public async Task Commit_KeepsDomainAndIntegrationEventsSeparate()
    {
        var (options, factory) = await CreateFactoryAsync();
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new PostgresOutboxFlush(collector);
        var streamId = NewStreamId();
        collector.Collect(new OrderPlacedIntegrationEvent("order-1"), CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);
            await flush.FlushAsync(tx, Ct);
            await tx.CommitAsync(Ct);
        }

        (await CountEventsAsync(options, streamId)).Should().Be(2);
        (await CountOutboxRowsAsync(options)).Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_NewStream_ReturnsCorrectVersionAndGlobalPosition()
    {
        var (_, factory) = await CreateFactoryAsync();
        var streamId = NewStreamId();

        await using var tx = await factory.BeginAsync(Ct);
        var result = await tx.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(3), Ct);
        await tx.CommitAsync(Ct);

        result.NextExpectedVersion.Should().Be(2);
        result.LastGlobalPosition.Value.Should().Be(3);
        result.Deduplicated.Should().BeFalse();
    }

    [Fact]
    public async Task AppendAsync_ExpectedVersionMismatch_ThrowsConcurrencyException()
    {
        var (_, factory) = await CreateFactoryAsync();
        var streamId = NewStreamId();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);

        var ex = (await Awaiting(
            () => tx.AppendAsync(streamId, 5, TestEvents.Distinct(1), Ct).AsTask()).Should().ThrowAsync<ConcurrencyException>()).Which;

        ex.StreamId.Should().Be(streamId);
        ex.ExpectedVersion.Should().Be(5);
        ex.ActualVersion.Should().Be(0);
    }

    [Fact]
    public async Task AppendAsync_DuplicateEventId_DeduplicatesWithinTransaction()
    {
        var (_, factory) = await CreateFactoryAsync();
        var streamId = NewStreamId();
        var eventId = Guid.NewGuid();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync(streamId, ExpectedVersion.NoStream, [TestEvents.OrderPlaced(eventId)], Ct);

        var result = await tx.AppendAsync(streamId, ExpectedVersion.Any, [TestEvents.OrderPlaced(eventId)], Ct);

        result.Deduplicated.Should().BeTrue();
        result.NextExpectedVersion.Should().Be(0);
    }

    [Fact]
    public async Task CommitAsync_CalledTwice_ThrowsInvalidOperationException()
    {
        var (_, factory) = await CreateFactoryAsync();
        var streamId = NewStreamId();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);
        await tx.CommitAsync(Ct);

        await Awaiting(() => tx.CommitAsync(Ct).AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppendAsync_AfterCommit_ThrowsInvalidOperationException()
    {
        var (_, factory) = await CreateFactoryAsync();
        var streamId = NewStreamId();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);
        await tx.CommitAsync(Ct);

        await Awaiting(
            () => tx.AppendAsync(streamId, ExpectedVersion.Any, TestEvents.Distinct(1), Ct).AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppendAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (_, factory) = await CreateFactoryAsync();
        var streamId = NewStreamId();
        var tx = await factory.BeginAsync(Ct);
        await tx.DisposeAsync();

        await Awaiting(
            () => tx.AppendAsync(streamId, ExpectedVersion.Any, TestEvents.Distinct(1), Ct).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task CommitAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var (_, factory) = await CreateFactoryAsync();
        var tx = await factory.BeginAsync(Ct);
        await tx.DisposeAsync();

        await Awaiting(() => tx.CommitAsync(Ct).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Commit_WithExplicitTimeProvider_StampsCreatedAt()
    {
        var fixedTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (options, factory) = await CreateFactoryAsync(new FixedTimeProvider(fixedTime));
        var streamId = NewStreamId();

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(1), Ct);
            await tx.CommitAsync(Ct);
        }

        (await ReadCreatedAtAsync(options, streamId)).Should().Be(fixedTime);
    }

    private static string NewStreamId() => $"order-{Guid.NewGuid():N}";

    private static EventMetadata CreateMetadata(Guid? messageId = null, string? tenantId = null) => new()
    {
        MessageId = messageId ?? Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
        TenantId = tenantId,
    };

    private async Task<(ActaPostgresOptions Options, PostgresEventAppendTransactionFactory Factory)> CreateFactoryAsync(
        TimeProvider? timeProvider = null)
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var factory = new PostgresEventAppendTransactionFactory(_fixture.DataSource, options, timeProvider);
        return (options, factory);
    }

    private async Task<long> CountEventsAsync(ActaPostgresOptions options, string streamId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {options.SchemaName}.events WHERE stream_id = @sid", connection);
        command.Parameters.AddWithValue("sid", streamId);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<long> CountOutboxRowsAsync(ActaPostgresOptions options)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {options.SchemaName}.outbox", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<DateTimeOffset> ReadCreatedAtAsync(ActaPostgresOptions options, string streamId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT created_at FROM {options.SchemaName}.events WHERE stream_id = @sid", connection);
        command.Parameters.AddWithValue("sid", streamId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task<OutboxRow> ReadSingleOutboxRowAsync(ActaPostgresOptions options)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT message_id, event_type, tenant_id FROM {options.SchemaName}.outbox", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        return new OutboxRow(reader.GetFieldValue<Guid>(0), reader.GetString(1), reader.GetString(2));
    }

    /// <summary>The three outbox columns these tests assert against (message_id/event_type/tenant_id).</summary>
    private readonly record struct OutboxRow(Guid MessageId, string EventType, string TenantId);
}
