using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.Coordination;
using Acta.Postgres.Tests.Infrastructure;

using Npgsql;

using Xunit;

namespace Acta.Postgres.Tests.Coordination;

/// <summary>
/// Integration facts for <see cref="AdvisoryLockLeaderElector"/> on a real PostgreSQL (Testcontainers):
/// per-slot single-active election on <c>pg_try_advisory_lock</c>, clean release, per-(projection,tenant)
/// isolation, and the ADR-005 core property — <b>session loss = lock loss = failover</b>. The full
/// ≥2-pod election/fencing/failover harness is task 7.6; these are the elector-level facts closing 7.5.
/// Advisory locks need no tables, so each test uses a fresh (unmigrated) schema name purely for the key
/// namespace.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdvisoryLockLeaderElectorIntegrationTests(PostgresFixture fixture)
{
    private const string Projection = "proj-1";

    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdvisoryLockLeaderElector NewElector(ActaPostgresOptions options)
        => new(_fixture.DataSource, options);

    private static ActaPostgresOptions FreshSchemaOptions()
        => new() { SchemaName = PostgresFixture.NewSchemaName() };

    [Fact]
    public async Task TryAcquire_WhenSlotFree_GrantsHeldLease()
    {
        var options = FreshSchemaOptions();
        var elector = NewElector(options);

        await using var lease = await elector.TryAcquireAsync(Projection, null, Ct);

        lease.Should().NotBeNull();
        lease!.ProjectionName.Should().Be(Projection);
        lease.TenantId.Should().BeNull();
        (await lease.IsHeldAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquire_WhenHeldByAnother_ReturnsNull()
    {
        var options = FreshSchemaOptions();
        // Two electors over the shared data source stand in for two pods (separate backend sessions).
        var podA = NewElector(options);
        var podB = NewElector(options);

        await using var leaseA = await podA.TryAcquireAsync(Projection, null, Ct);
        leaseA.Should().NotBeNull();

        // pod B cannot steal a slot pod A already holds — non-blocking try-lock returns null.
        var leaseB = await podB.TryAcquireAsync(Projection, null, Ct);
        leaseB.Should().BeNull();
    }

    [Fact]
    public async Task Dispose_ReleasesLock_EnablingReacquire()
    {
        var options = FreshSchemaOptions();
        var elector = NewElector(options);

        var first = await elector.TryAcquireAsync(Projection, null, Ct);
        first.Should().NotBeNull();
        await first!.DisposeAsync();

        // A clean release frees the slot; the next acquire (any pod) succeeds.
        await using var second = await elector.TryAcquireAsync(Projection, null, Ct);
        second.Should().NotBeNull();
    }

    [Fact]
    public async Task DifferentSlots_DoNotContend()
    {
        var options = FreshSchemaOptions();
        var elector = NewElector(options);

        // Leadership is per (projection, tenant): distinct projections and distinct tenants of the same
        // projection are independent slots, all acquirable at once.
        await using var projOne = await elector.TryAcquireAsync(Projection, null, Ct);
        await using var projTwo = await elector.TryAcquireAsync("proj-2", null, Ct);
        await using var projOneTenantB = await elector.TryAcquireAsync(Projection, "tenant-b", Ct);

        projOne.Should().NotBeNull();
        projTwo.Should().NotBeNull();
        projOneTenantB.Should().NotBeNull();
    }

    [Fact]
    public async Task DifferentSchemas_DoNotContend_KeyIsSchemaNamespaced()
    {
        // Same projection/tenant but two different schema namespaces (two Acta instances on one DB):
        // the SchemaName in the key (R3, sec scan #2) keeps their elections independent.
        var elA = NewElector(FreshSchemaOptions());
        var elB = NewElector(FreshSchemaOptions());

        await using var leaseA = await elA.TryAcquireAsync(Projection, null, Ct);
        await using var leaseB = await elB.TryAcquireAsync(Projection, null, Ct);

        leaseA.Should().NotBeNull();
        leaseB.Should().NotBeNull();
    }

    [Fact]
    public async Task SessionLoss_ReleasesLock_EnablingFailover()
    {
        var options = FreshSchemaOptions();
        var podA = NewElector(options);
        var podB = NewElector(options);

        var leaseA = await podA.TryAcquireAsync(Projection, null, Ct);
        leaseA.Should().NotBeNull();
        var backendPid = ((AdvisoryLockLease)leaseA!).BackendProcessId;

        // Kill pod A's backend abruptly (crash, not a clean release) from a separate admin session.
        await TerminateBackendAsync(backendPid);

        // The lost session releases the advisory lock in PostgreSQL; pod A observes it is no longer held.
        (await leaseA.IsHeldAsync(Ct)).Should().BeFalse();

        // Failover: pod B takes leadership over from the released slot.
        await using var leaseB = await AcquireWithRetryAsync(podB, Projection, null);
        leaseB.Should().NotBeNull();

        // Disposing the dead lease is safe (its session is already gone).
        await leaseA.DisposeAsync();
    }

    /// <summary>
    /// After an abrupt backend kill, PostgreSQL releases the terminated session's locks
    /// asynchronously; poll the failover acquire briefly so the test is deterministic.
    /// </summary>
    private static async Task<ILeadershipLease?> AcquireWithRetryAsync(
        AdvisoryLockLeaderElector elector, string projection, string? tenantId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var lease = await elector.TryAcquireAsync(projection, tenantId, Ct);
            if (lease is not null)
            {
                return lease;
            }

            await Task.Delay(100, Ct);
        }

        return null;
    }

    private async Task TerminateBackendAsync(int backendPid)
    {
        await using var admin = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin);
        command.Parameters.AddWithValue("pid", backendPid);
        await command.ExecuteScalarAsync(Ct);
    }
}
