using Acta.Abstractions;

using Xunit;

namespace Acta.Tests.Reservations.Contracts;

/// <summary>
/// The shared, written-once contract suite for <see cref="IReservationStore"/> (R3 pattern,
/// TESTING-SPEC §5.1/§6.1). Every backend supplies a fresh store through <see cref="CreateStoreAsync"/>
/// and inherits these facts unchanged: the Postgres backend via
/// <c>PostgresReservationStoreContractTests</c> now; the in-memory backend (task 8.5) will add its own
/// concrete subclass. Each fact asserts observable behavior (the boolean reserve outcome, the effect
/// of a later operation) — never mere compilation. An "already-expired" reservation is modeled with a
/// non-positive TTL: its <c>expires_at</c> lands strictly in the past, a backend-agnostic,
/// deterministic way to reach the lazy-takeover path without a wall-clock wait.
/// </summary>
public abstract class ReservationStoreContractTests
{
    private const string Scope = "email";

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlreadyExpired = TimeSpan.FromSeconds(-1);

    /// <summary>Produces a fresh, empty store for a single test — backend-specific.</summary>
    protected abstract ValueTask<IReservationStore> CreateStoreAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryReserve_OnFreeValue_ReturnsTrue()
    {
        var store = await CreateStoreAsync();

        var reserved = await store.TryReserveAsync(Scope, "free@acta.io", "owner-a", Ttl, ct: Ct);

        reserved.Should().BeTrue();
    }

    [Fact]
    public async Task TryReserve_WhenActivelyReserved_ReturnsFalse()
    {
        var store = await CreateStoreAsync();
        await store.TryReserveAsync(Scope, "taken@acta.io", "owner-a", Ttl, ct: Ct);

        var second = await store.TryReserveAsync(Scope, "taken@acta.io", "owner-b", Ttl, ct: Ct);

        second.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_MakesReservationPermanent_BlocksExpiredTakeover()
    {
        var store = await CreateStoreAsync();
        // Reserve with an already-elapsed TTL, then confirm: confirmation clears the TTL, so the value
        // can no longer be taken over even though it "expired".
        await store.TryReserveAsync(Scope, "perm@acta.io", "owner-a", AlreadyExpired, ct: Ct);
        await store.ConfirmAsync(Scope, "perm@acta.io", "owner-a", ct: Ct);

        var takeover = await store.TryReserveAsync(Scope, "perm@acta.io", "owner-b", Ttl, ct: Ct);

        takeover.Should().BeFalse();
    }

    [Fact]
    public async Task Release_FreesUnconfirmedReservation_AllowsReReserve()
    {
        var store = await CreateStoreAsync();
        await store.TryReserveAsync(Scope, "rel@acta.io", "owner-a", Ttl, ct: Ct);

        await store.ReleaseAsync(Scope, "rel@acta.io", "owner-a", ct: Ct);
        var reReserved = await store.TryReserveAsync(Scope, "rel@acta.io", "owner-b", Ttl, ct: Ct);

        reReserved.Should().BeTrue();
    }

    [Fact]
    public async Task TryReserve_TakesOverExpiredUnconfirmedReservation_ReturnsTrue()
    {
        var store = await CreateStoreAsync();
        await store.TryReserveAsync(Scope, "exp@acta.io", "owner-a", AlreadyExpired, ct: Ct);

        var takeover = await store.TryReserveAsync(Scope, "exp@acta.io", "owner-b", Ttl, ct: Ct);

        takeover.Should().BeTrue();
    }

    [Fact]
    public async Task TryReserve_SameValueUnderDifferentTenants_BothSucceed()
    {
        var store = await CreateStoreAsync();

        var tenantA = await store.TryReserveAsync(Scope, "shared@acta.io", "owner", Ttl, tenantId: "tenant-a", ct: Ct);
        var tenantB = await store.TryReserveAsync(Scope, "shared@acta.io", "owner", Ttl, tenantId: "tenant-b", ct: Ct);

        tenantA.Should().BeTrue();
        tenantB.Should().BeTrue();
    }

    [Fact]
    public async Task Release_ByNonOwner_IsNoOp_ValueStaysReserved()
    {
        var store = await CreateStoreAsync();
        await store.TryReserveAsync(Scope, "guard@acta.io", "owner-a", Ttl, ct: Ct);

        // The wrong owner cannot release someone else's reservation.
        await store.ReleaseAsync(Scope, "guard@acta.io", "owner-b", ct: Ct);
        var stillReserved = await store.TryReserveAsync(Scope, "guard@acta.io", "owner-c", Ttl, ct: Ct);

        stillReserved.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_ByNonOwner_IsNoOp_DoesNotBlockExpiredTakeover()
    {
        var store = await CreateStoreAsync();
        await store.TryReserveAsync(Scope, "cguard@acta.io", "owner-a", AlreadyExpired, ct: Ct);

        // The wrong owner cannot confirm someone else's reservation — it stays unconfirmed & expired,
        // so the legitimate takeover path still succeeds.
        await store.ConfirmAsync(Scope, "cguard@acta.io", "owner-b", ct: Ct);
        var takeover = await store.TryReserveAsync(Scope, "cguard@acta.io", "owner-c", Ttl, ct: Ct);

        takeover.Should().BeTrue();
    }
}
