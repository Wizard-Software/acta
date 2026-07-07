using System.Diagnostics.CodeAnalysis;

using Acta.Postgres.Reservations;
using Acta.Postgres.Tests.Infrastructure;

using Npgsql;

using Xunit;

namespace Acta.Postgres.Tests.Reservations;

/// <summary>
/// Postgres-specific reservation facts closing the TESTING-SPEC §6.1 catalogue against the real
/// backend: TTL expiry during confirm (owner-guarded no-op after takeover), takeover after expiry,
/// per-tenant isolation, and cross-pod visibility (two store instances over the shared schema stand in
/// for two pods). Expiry is reached deterministically with a non-positive TTL — its <c>expires_at</c>
/// lands strictly in the past, so the server-clock takeover guard (<c>expires_at &lt; now()</c>) fires
/// without a wall-clock wait.
/// </summary>
[Collection(PostgresCollection.Name)]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The read helper interpolates only the schema name produced by PostgresFixture.NewSchemaName() " +
        "(an allow-list-valid acta_test_<guid>), never external input; the scope/value travel as " +
        "NpgsqlParameter. Same sanctioned identifier-interpolation exception as the production store.")]
public sealed class PostgresReservationStoreIntegrationTests(PostgresFixture fixture)
{
    private const string Scope = "email";

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlreadyExpired = TimeSpan.FromSeconds(-1);

    private readonly PostgresFixture _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TakeoverAfterExpiry_SwitchesOwnership_ToTheNewOwner()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var store = new PostgresReservationStore(_fixture.DataSource, options);

        (await store.TryReserveAsync(Scope, "x@acta.io", "owner-a", AlreadyExpired, ct: Ct)).Should().BeTrue();
        (await store.TryReserveAsync(Scope, "x@acta.io", "owner-b", Ttl, ct: Ct)).Should().BeTrue();

        var (owner, confirmed) = await ReadRowAsync(options.SchemaName, "x@acta.io");
        owner.Should().Be("owner-b");
        confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task TtlExpiryDuringConfirm_ConfirmByOriginalOwnerAfterTakeover_IsNoOp()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var store = new PostgresReservationStore(_fixture.DataSource, options);

        // A reserves; the reservation expires; B takes it over before A confirms.
        (await store.TryReserveAsync(Scope, "y@acta.io", "owner-a", AlreadyExpired, ct: Ct)).Should().BeTrue();
        (await store.TryReserveAsync(Scope, "y@acta.io", "owner-b", Ttl, ct: Ct)).Should().BeTrue();

        // A's late confirm targets a reservation it no longer owns → owner-guarded silent no-op.
        await store.ConfirmAsync(Scope, "y@acta.io", "owner-a", ct: Ct);

        var afterA = await ReadRowAsync(options.SchemaName, "y@acta.io");
        afterA.Owner.Should().Be("owner-b");
        afterA.Confirmed.Should().BeFalse();

        // B, the rightful owner, can still confirm → makes it permanent.
        await store.ConfirmAsync(Scope, "y@acta.io", "owner-b", ct: Ct);
        var afterB = await ReadRowAsync(options.SchemaName, "y@acta.io");
        afterB.Owner.Should().Be("owner-b");
        afterB.Confirmed.Should().BeTrue();
    }

    [Fact]
    public async Task TenantIsolation_SameValueDistinctTenants_AreIndependent()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        var store = new PostgresReservationStore(_fixture.DataSource, options);

        (await store.TryReserveAsync(Scope, "shared@acta.io", "owner-a", Ttl, tenantId: "tenant-a", ct: Ct)).Should().BeTrue();
        (await store.TryReserveAsync(Scope, "shared@acta.io", "owner-b", Ttl, tenantId: "tenant-b", ct: Ct)).Should().BeTrue();

        // Confirming tenant A's reservation must not touch tenant B's.
        await store.ConfirmAsync(Scope, "shared@acta.io", "owner-a", tenantId: "tenant-a", ct: Ct);

        (await store.TryReserveAsync(Scope, "shared@acta.io", "owner-c", Ttl, tenantId: "tenant-a", ct: Ct)).Should().BeFalse();
        (await store.TryReserveAsync(Scope, "shared@acta.io", "owner-c", Ttl, tenantId: "tenant-b", ct: Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task CrossPodVisibility_ReservationOnOnePod_IsSeenByAnother()
    {
        var options = await PostgresSchemaSetup.MigrateFreshSchemaAsync(_fixture, Ct);
        // Two store instances over the shared schema stand in for two pods (separate pooled connections).
        var podA = new PostgresReservationStore(_fixture.DataSource, options);
        var podB = new PostgresReservationStore(_fixture.DataSource, options);

        (await podA.TryReserveAsync(Scope, "z@acta.io", "owner-a", Ttl, ct: Ct)).Should().BeTrue();

        // Pod B sees pod A's reservation through the shared database → cannot reserve the same value.
        (await podB.TryReserveAsync(Scope, "z@acta.io", "owner-b", Ttl, ct: Ct)).Should().BeFalse();
    }

    private async Task<(string Owner, bool Confirmed)> ReadRowAsync(string schema, string value)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            $"SELECT owner_id, confirmed FROM {schema}.reservations WHERE scope = @scope AND value = @value",
            connection);
        command.Parameters.AddWithValue("scope", Scope);
        command.Parameters.AddWithValue("value", value);

        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        return (reader.GetString(0), reader.GetBoolean(1));
    }
}
