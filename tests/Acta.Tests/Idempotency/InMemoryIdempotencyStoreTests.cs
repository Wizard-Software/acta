using Xunit;

using Microsoft.Extensions.Time.Testing;

using Acta.InMemory;

namespace Acta.Tests.Idempotency;

/// <summary>
/// Backend-specific facts for <see cref="InMemoryIdempotencyStore"/> that the shared
/// <c>IdempotencyStoreContractTests</c> suite (backend-agnostic) does not exercise: honoring an
/// explicitly-injected <see cref="TimeProvider"/> rather than silently falling back to
/// <see cref="TimeProvider.System"/>, the exact expiry boundary (an entry expiring at precisely
/// "now" is expired, not still active), and per-method argument/cancellation guards on
/// <c>GetResultAsync</c>/<c>SaveResultAsync</c>. A frozen <see cref="FakeTimeProvider"/> drives every
/// scenario deterministically — no wall-clock waits.
/// </summary>
public sealed class InMemoryIdempotencyStoreTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryRegister_UsesInjectedClock_NotSystemClock_ForExpiry()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryIdempotencyStore(clock);
        await store.TryRegisterAsync("cmd-clock", TimeSpan.FromMinutes(5), ct: Ct);

        // Fast-forward the INJECTED clock only — no real (wall-clock) time passes at all. A correct
        // implementation must see this registration as expired via the injected clock; a store that
        // silently fell back to TimeProvider.System would instead see it as still active (its real
        // expiry, computed from the real system clock, is minutes away) and report a duplicate.
        clock.Advance(TimeSpan.FromMinutes(10));

        var reRegistered = await store.TryRegisterAsync("cmd-clock", Retention, ct: Ct);

        reRegistered.Should().BeTrue();
    }

    [Fact]
    public async Task TryRegister_ExpiresAtExactlyEqualsNow_IsTreatedAsExpired_AllowsReRegistration()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryIdempotencyStore(clock);
        // Retention of zero sets ExpiresAt to exactly "now". The clock is never advanced, so the
        // second call observes the identical instant — the exact boundary between "still active" (>)
        // and "already expired" (>=).
        await store.TryRegisterAsync("cmd-boundary", TimeSpan.Zero, ct: Ct);

        var reRegistered = await store.TryRegisterAsync("cmd-boundary", Retention, ct: Ct);

        reRegistered.Should().BeTrue();
    }

    [Fact]
    public async Task TryRegister_EmptyKey_ThrowsArgumentException()
    {
        var store = new InMemoryIdempotencyStore();

        await Awaiting(() => store.TryRegisterAsync(string.Empty, Retention, ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryRegister_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryIdempotencyStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => store.TryRegisterAsync("cmd-x", Retention, ct: cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetResult_EmptyKey_ThrowsArgumentException()
    {
        var store = new InMemoryIdempotencyStore();

        await Awaiting(() => store.GetResultAsync(string.Empty, ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetResult_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryIdempotencyStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => store.GetResultAsync("cmd-x", ct: cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SaveResult_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryIdempotencyStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => store.SaveResultAsync("cmd-x", new byte[] { 1 }, ct: cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
