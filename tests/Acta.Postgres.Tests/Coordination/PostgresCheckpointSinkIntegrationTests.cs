using System.Diagnostics.CodeAnalysis;

using Acta.Abstractions;
using Acta.Postgres.Coordination;
using Acta.Postgres.Tests.Infrastructure;

using Npgsql;

using Xunit;

namespace Acta.Postgres.Tests.Coordination;

/// <summary>
/// Postgres-specific checkpoint facts closing the fencing behaviour deferred from the in-memory suite
/// (TESTING-SPEC §5.2): the zombie-CAS rejection, the strictly-ahead takeover the §3.3 grant allows, the
/// advance-only rollback guard, and cross-pod visibility. These are the sink-level facts — the full
/// advisory-lock failover harness (<c>AcquireLeadershipAsync</c>/<c>KillConnectionAsync</c>) belongs to
/// tasks 7.5/7.6. A "zombie" here is a stale <c>owner_token</c> whose non-advancing write must be fenced.
/// </summary>
[Collection(PostgresCollection.Name)]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The read-back helper interpolates only the schema name produced by PostgresFixture.NewSchemaName() " +
        "(an allow-list-valid acta_test_<guid>), never external input; the projection name travels as an " +
        "NpgsqlParameter. Same sanctioned identifier-interpolation exception as the production sink.")]
public sealed class PostgresCheckpointSinkIntegrationTests(PostgresFixture fixture)
{
    private const string Projection = "proj-1";
    private const string OwnerA = "owner-a";
    private const string OwnerB = "owner-b";

    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Checkpoint_ZombieLeader_CannotOverwriteAfterFailover()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var sink = new PostgresCheckpointSink(_fixture.DataSource, options);

        // A leads and checkpoints; B takes over by advancing strictly ahead (§3.3 position < @new grant).
        await sink.SaveAsync(Projection, null, new GlobalPosition(50), OwnerA, Ct);
        await sink.SaveAsync(Projection, null, new GlobalPosition(100), OwnerB, Ct);

        // A is now a zombie (stale token); its lower, non-advancing write is fenced.
        var act = () => sink.SaveAsync(Projection, null, new GlobalPosition(90), OwnerA, Ct).AsTask();

        var fenced = (await act.Should().ThrowAsync<CheckpointFencedException>()).Which;
        fenced.ProjectionName.Should().Be(Projection);
        fenced.OwnerToken.Should().Be(OwnerA);
    }

    [Fact]
    public async Task Save_ZombieRejected_LeavesCheckpointIntact()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var sink = new PostgresCheckpointSink(_fixture.DataSource, options);

        await sink.SaveAsync(Projection, null, new GlobalPosition(50), OwnerA, Ct);
        await sink.SaveAsync(Projection, null, new GlobalPosition(100), OwnerB, Ct);
        await Awaiting(() => sink.SaveAsync(Projection, null, new GlobalPosition(90), OwnerA, Ct).AsTask())
            .Should().ThrowAsync<CheckpointFencedException>();

        // The fenced write must not have rolled the checkpoint back.
        (await sink.LoadAsync(Projection, null, Ct)).Should().Be(new GlobalPosition(100));
    }

    [Fact]
    public async Task Save_TakeoverByAdvancingOwner_SwitchesOwnerToken()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var sink = new PostgresCheckpointSink(_fixture.DataSource, options);

        await sink.SaveAsync(Projection, null, new GlobalPosition(50), OwnerA, Ct);
        await sink.SaveAsync(Projection, null, new GlobalPosition(100), OwnerB, Ct);

        var (owner, position) = await ReadRowAsync(options.SchemaName, Projection);
        owner.Should().Be(OwnerB);
        position.Should().Be(100);
    }

    [Fact]
    public async Task Save_BackwardBySameOwner_Throws_AndKeepsPosition()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var sink = new PostgresCheckpointSink(_fixture.DataSource, options);

        await sink.SaveAsync(Projection, null, new GlobalPosition(100), OwnerA, Ct);

        // The legitimate owner may not roll the checkpoint backward (advance-only, ADR-005).
        await Awaiting(() => sink.SaveAsync(Projection, null, new GlobalPosition(90), OwnerA, Ct).AsTask())
            .Should().ThrowAsync<InvalidOperationException>();

        (await sink.LoadAsync(Projection, null, Ct)).Should().Be(new GlobalPosition(100));
    }

    [Fact]
    public async Task Save_ZombieClassify_IsTenantScoped_NoCrossTenantLeak()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var sink = new PostgresCheckpointSink(_fixture.DataSource, options);

        // Same projection name in two tenants; only tenant-a fails over to owner-b.
        await sink.SaveAsync(Projection, "tenant-a", new GlobalPosition(50), OwnerA, Ct);
        await sink.SaveAsync(Projection, "tenant-a", new GlobalPosition(100), OwnerB, Ct);
        await sink.SaveAsync(Projection, "tenant-b", new GlobalPosition(10), "owner-c", Ct);

        // tenant-a zombie is fenced; the exception carries its own token, never tenant-b's owner-c.
        var act = () => sink.SaveAsync(Projection, "tenant-a", new GlobalPosition(90), OwnerA, Ct).AsTask();
        var fenced = (await act.Should().ThrowAsync<CheckpointFencedException>()).Which;
        fenced.OwnerToken.Should().Be(OwnerA);

        // tenant-b is untouched by tenant-a's failover.
        (await sink.LoadAsync(Projection, "tenant-b", Ct)).Should().Be(new GlobalPosition(10));
    }

    [Fact]
    public async Task CrossPodVisibility_SaveOnOneInstance_SeenByAnother()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        // Two sink instances over the shared schema stand in for two pods (separate pooled connections).
        var podA = new PostgresCheckpointSink(_fixture.DataSource, options);
        var podB = new PostgresCheckpointSink(_fixture.DataSource, options);

        await podA.SaveAsync(Projection, null, new GlobalPosition(42), OwnerA, Ct);

        (await podB.LoadAsync(Projection, null, Ct)).Should().Be(new GlobalPosition(42));
    }

    private async Task<(string Owner, long Position)> ReadRowAsync(string schema, string projection)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT owner_token, position FROM {schema}.checkpoints WHERE projection_name = @projection AND tenant_id = ''",
            connection);
        command.Parameters.AddWithValue("projection", projection);

        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        return (reader.GetString(0), reader.GetInt64(1));
    }
}
