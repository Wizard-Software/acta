using Xunit;

using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Coordination;

/// <summary>
/// Unit tests for <see cref="InMemoryLeaderElector"/> (task 7.5, ADR-005): single-active
/// acquisition per <c>(projection, tenant)</c> slot, null-tenant normalization to the
/// single-tenant slot, argument/cancellation guards, lease identity, <see cref="ILeadershipLease.IsHeldAsync"/>
/// lifetime, and idempotent release that frees the slot for re-acquisition. These are the unit
/// counterpart to the PostgreSQL advisory-lock integration tests (task 7.5) — the mutation gate
/// mutates the core <c>Acta</c> project and only <c>Acta.Tests</c> kills those mutants.
/// </summary>
public sealed class InMemoryLeaderElectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryAcquireAsync_FreeSlot_ReturnsLeaseCarryingProjectionAndTenant()
    {
        var elector = new InMemoryLeaderElector();

        await using var lease = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);

        lease.Should().NotBeNull();
        lease!.ProjectionName.Should().Be("projection-a");
        lease.TenantId.Should().Be("tenant-1");
    }

    [Fact]
    public async Task TryAcquireAsync_NullTenant_LeasePreservesNullTenant()
    {
        var elector = new InMemoryLeaderElector();

        await using var lease = await elector.TryAcquireAsync("projection-a", tenantId: null, Ct);

        lease.Should().NotBeNull();
        lease!.TenantId.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task TryAcquireAsync_NullOrEmptyProjectionName_ThrowsArgumentException(string? projectionName)
    {
        var elector = new InMemoryLeaderElector();

        await Awaiting(() => elector.TryAcquireAsync(projectionName!, "tenant-1", Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryAcquireAsync_CancelledToken_Throws()
    {
        var elector = new InMemoryLeaderElector();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => elector.TryAcquireAsync("projection-a", "tenant-1", cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TryAcquireAsync_SlotAlreadyHeld_ReturnsNull()
    {
        var elector = new InMemoryLeaderElector();
        await using var first = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);
        first.Should().NotBeNull();

        var second = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);

        second.Should().BeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_DifferentTenantsSameProjection_BothAcquire()
    {
        var elector = new InMemoryLeaderElector();

        await using var a = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);
        await using var b = await elector.TryAcquireAsync("projection-a", "tenant-2", Ct);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_DifferentProjectionsSameTenant_BothAcquire()
    {
        var elector = new InMemoryLeaderElector();

        await using var a = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);
        await using var b = await elector.TryAcquireAsync("projection-b", "tenant-1", Ct);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
    }

    [Fact]
    public async Task TryAcquireAsync_NullTenantThenEmptyTenant_MapToTheSameSlot()
    {
        // A null tenant normalizes to the single-tenant slot (""), so an explicit "" for the same
        // projection is the SAME slot and must be refused while the first lease is held.
        var elector = new InMemoryLeaderElector();
        await using var withNull = await elector.TryAcquireAsync("projection-a", tenantId: null, Ct);
        withNull.Should().NotBeNull();

        var withEmpty = await elector.TryAcquireAsync("projection-a", tenantId: "", Ct);

        withEmpty.Should().BeNull();
    }

    [Fact]
    public async Task IsHeldAsync_WhileLeaseHeld_ReturnsTrue()
    {
        var elector = new InMemoryLeaderElector();
        await using var lease = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);

        (await lease!.IsHeldAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task IsHeldAsync_AfterDispose_ReturnsFalse()
    {
        var elector = new InMemoryLeaderElector();
        var lease = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);
        lease.Should().NotBeNull();

        await lease!.DisposeAsync();

        (await lease.IsHeldAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task IsHeldAsync_CancelledToken_Throws()
    {
        var elector = new InMemoryLeaderElector();
        await using var lease = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => lease!.IsHeldAsync(cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DisposeAsync_ReleasesSlot_AllowingReacquisition()
    {
        var elector = new InMemoryLeaderElector();
        var first = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);
        first.Should().NotBeNull();

        await first!.DisposeAsync();
        await using var reacquired = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);

        reacquired.Should().NotBeNull();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotentAndKeepsSlotReleased()
    {
        var elector = new InMemoryLeaderElector();
        var lease = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);
        lease.Should().NotBeNull();

        await lease!.DisposeAsync();
        await lease.DisposeAsync(); // second dispose must not throw and must not re-take the slot

        await using var reacquired = await elector.TryAcquireAsync("projection-a", "tenant-1", Ct);
        reacquired.Should().NotBeNull();
    }
}
