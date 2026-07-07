using Acta.Abstractions;

using Xunit;

namespace Acta.Tests.Idempotency.Contracts;

/// <summary>
/// The shared, written-once contract suite for <see cref="IIdempotencyStore"/> (R3 pattern,
/// TESTING-SPEC §5.1/§6.1). Every backend supplies a fresh store through <see cref="CreateStoreAsync"/>
/// and inherits these facts unchanged: the Postgres backend via
/// <c>PostgresIdempotencyStoreContractTests</c> now; the in-memory backend (task 8.5) later. An
/// "already-expired" entry is modeled with a non-positive retention: its <c>expires_at</c> lands
/// strictly in the past, a backend-agnostic, deterministic way to reach the lazy re-registration path.
/// </summary>
public abstract class IdempotencyStoreContractTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlreadyExpired = TimeSpan.FromSeconds(-1);

    /// <summary>Produces a fresh, empty store for a single test — backend-specific.</summary>
    protected abstract ValueTask<IIdempotencyStore> CreateStoreAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryRegister_ForNewKey_ReturnsTrue()
    {
        var store = await CreateStoreAsync();

        var first = await store.TryRegisterAsync("cmd-new", Retention, ct: Ct);

        first.Should().BeTrue();
    }

    [Fact]
    public async Task TryRegister_ForActiveDuplicate_ReturnsFalse()
    {
        var store = await CreateStoreAsync();
        await store.TryRegisterAsync("cmd-dup", Retention, ct: Ct);

        var duplicate = await store.TryRegisterAsync("cmd-dup", Retention, ct: Ct);

        duplicate.Should().BeFalse();
    }

    [Fact]
    public async Task SaveResult_ThenGetResult_ReturnsSameBytes()
    {
        var store = await CreateStoreAsync();
        await store.TryRegisterAsync("cmd-res", Retention, ct: Ct);
        byte[] result = [0x01, 0x02, 0x03, 0x04];

        await store.SaveResultAsync("cmd-res", result, ct: Ct);
        var fetched = await store.GetResultAsync("cmd-res", ct: Ct);

        fetched.HasValue.Should().BeTrue();
        fetched!.Value.ToArray().Should().Equal(result);
    }

    [Fact]
    public async Task GetResult_AfterRegisterWithoutSave_ReturnsNull()
    {
        var store = await CreateStoreAsync();
        await store.TryRegisterAsync("cmd-noresult", Retention, ct: Ct);

        var fetched = await store.GetResultAsync("cmd-noresult", ct: Ct);

        fetched.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task GetResult_ForUnknownKey_ReturnsNull()
    {
        var store = await CreateStoreAsync();

        var fetched = await store.GetResultAsync("cmd-unknown", ct: Ct);

        fetched.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task TryRegister_TakesOverExpiredEntry_ReturnsTrue()
    {
        var store = await CreateStoreAsync();
        await store.TryRegisterAsync("cmd-exp", AlreadyExpired, ct: Ct);

        var reRegistered = await store.TryRegisterAsync("cmd-exp", Retention, ct: Ct);

        reRegistered.Should().BeTrue();
    }

    [Fact]
    public async Task TryRegister_SameKeyUnderDifferentTenants_BothSucceed()
    {
        var store = await CreateStoreAsync();

        var tenantA = await store.TryRegisterAsync("cmd-shared", Retention, tenantId: "tenant-a", ct: Ct);
        var tenantB = await store.TryRegisterAsync("cmd-shared", Retention, tenantId: "tenant-b", ct: Ct);

        tenantA.Should().BeTrue();
        tenantB.Should().BeTrue();
    }
}
