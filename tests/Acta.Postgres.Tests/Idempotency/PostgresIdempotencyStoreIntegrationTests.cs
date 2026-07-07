using Acta.Postgres.Idempotency;
using Acta.Postgres.Tests.Infrastructure;

using Xunit;

namespace Acta.Postgres.Tests.Idempotency;

/// <summary>
/// Postgres-specific idempotency facts (TESTING-SPEC §6.1): per-tenant isolation of the same command
/// key, lazy re-registration of an expired entry (a retry after retention), and a cross-pod idempotent
/// retry that returns the remembered result (two store instances over the shared schema stand in for
/// two pods). Expiry is reached deterministically with a non-positive retention.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresIdempotencyStoreIntegrationTests(PostgresFixture fixture)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlreadyExpired = TimeSpan.FromSeconds(-1);

    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TenantIsolation_SameKeyTwoTenants_BothExecute()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var store = new PostgresIdempotencyStore(_fixture.DataSource, options);

        (await store.TryRegisterAsync("cmd", Retention, tenantId: "tenant-a", ct: Ct)).Should().BeTrue();
        (await store.TryRegisterAsync("cmd", Retention, tenantId: "tenant-b", ct: Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task ExpiredEntry_RetryAfterRetention_ReRegisters()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var store = new PostgresIdempotencyStore(_fixture.DataSource, options);

        (await store.TryRegisterAsync("cmd", AlreadyExpired, ct: Ct)).Should().BeTrue();
        (await store.TryRegisterAsync("cmd", Retention, ct: Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task CrossPod_RetryOfRegisteredCommand_IsDuplicate_AndSeesRememberedResult()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var podA = new PostgresIdempotencyStore(_fixture.DataSource, options);
        var podB = new PostgresIdempotencyStore(_fixture.DataSource, options);

        (await podA.TryRegisterAsync("cmd", Retention, ct: Ct)).Should().BeTrue();
        byte[] result = [0xAA, 0xBB, 0xCC];
        await podA.SaveResultAsync("cmd", result, ct: Ct);

        // Pod B retries the same command: the active registration is visible through the shared DB, so
        // it must NOT re-execute — and it can read the result pod A saved.
        (await podB.TryRegisterAsync("cmd", Retention, ct: Ct)).Should().BeFalse();
        var remembered = await podB.GetResultAsync("cmd", ct: Ct);

        remembered.HasValue.Should().BeTrue();
        remembered!.Value.ToArray().Should().Equal(result);
    }
}
