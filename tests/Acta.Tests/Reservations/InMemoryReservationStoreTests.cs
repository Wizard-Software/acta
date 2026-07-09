using Xunit;

using Microsoft.Extensions.Time.Testing;

using Acta.InMemory;

namespace Acta.Tests.Reservations;

/// <summary>
/// Backend-specific facts for <see cref="InMemoryReservationStore"/> that the shared
/// <c>ReservationStoreContractTests</c> suite (backend-agnostic) does not exercise: honoring an
/// explicitly-injected <see cref="TimeProvider"/> rather than silently falling back to
/// <see cref="TimeProvider.System"/>, the exact expiry boundary (an entry expiring at precisely
/// "now" is expired, not still active), and per-method argument/cancellation guards on
/// <c>ConfirmAsync</c>/<c>ReleaseAsync</c> (the constructor/<c>TryReserveAsync</c> guards are covered
/// elsewhere). A frozen <see cref="FakeTimeProvider"/> drives every scenario deterministically — no
/// wall-clock waits.
/// </summary>
public sealed class InMemoryReservationStoreTests
{
    private const string Scope = "email";

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryReserve_UsesInjectedClock_NotSystemClock_ForExpiry()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryReservationStore(clock);
        await store.TryReserveAsync(Scope, "clock@acta.io", "owner-a", TimeSpan.FromMinutes(5), ct: Ct);

        // Fast-forward the INJECTED clock only — no real (wall-clock) time passes at all. A correct
        // implementation must see this reservation as expired via the injected clock; a store that
        // silently fell back to TimeProvider.System would instead see it as still active (its real
        // expiry, computed from the real system clock, is minutes away) and report a collision.
        clock.Advance(TimeSpan.FromMinutes(10));

        var takeover = await store.TryReserveAsync(Scope, "clock@acta.io", "owner-b", Ttl, ct: Ct);

        takeover.Should().BeTrue();
    }

    [Fact]
    public async Task TryReserve_ExpiresAtExactlyEqualsNow_IsTreatedAsExpired_AllowsTakeover()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryReservationStore(clock);
        // TTL of zero sets ExpiresAt to exactly "now". The clock is never advanced, so the second
        // call observes the identical instant — the exact boundary between "still active" (>) and
        // "already expired" (>=).
        await store.TryReserveAsync(Scope, "boundary@acta.io", "owner-a", TimeSpan.Zero, ct: Ct);

        var takeover = await store.TryReserveAsync(Scope, "boundary@acta.io", "owner-b", Ttl, ct: Ct);

        takeover.Should().BeTrue();
    }

    [Fact]
    public async Task TryReserve_EmptyScope_ThrowsArgumentException()
    {
        var store = new InMemoryReservationStore();

        await Awaiting(() => store.TryReserveAsync(string.Empty, "v", "owner-a", Ttl, ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryReserve_EmptyValue_ThrowsArgumentException()
    {
        var store = new InMemoryReservationStore();

        await Awaiting(() => store.TryReserveAsync(Scope, string.Empty, "owner-a", Ttl, ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryReserve_EmptyOwnerId_ThrowsArgumentException()
    {
        var store = new InMemoryReservationStore();

        await Awaiting(() => store.TryReserveAsync(Scope, "v", string.Empty, Ttl, ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Confirm_EmptyScope_ThrowsArgumentException()
    {
        var store = new InMemoryReservationStore();

        await Awaiting(() => store.ConfirmAsync(string.Empty, "v", "owner-a", ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Confirm_EmptyValue_ThrowsArgumentException()
    {
        var store = new InMemoryReservationStore();

        await Awaiting(() => store.ConfirmAsync(Scope, string.Empty, "owner-a", ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Confirm_EmptyOwnerId_ThrowsArgumentException()
    {
        var store = new InMemoryReservationStore();

        await Awaiting(() => store.ConfirmAsync(Scope, "v", string.Empty, ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Confirm_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryReservationStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => store.ConfirmAsync(Scope, "v", "owner-a", ct: cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Release_EmptyScope_ThrowsArgumentException()
    {
        var store = new InMemoryReservationStore();

        await Awaiting(() => store.ReleaseAsync(string.Empty, "v", "owner-a", ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Release_EmptyValue_ThrowsArgumentException()
    {
        var store = new InMemoryReservationStore();

        await Awaiting(() => store.ReleaseAsync(Scope, string.Empty, "owner-a", ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Release_EmptyOwnerId_ThrowsArgumentException()
    {
        var store = new InMemoryReservationStore();

        await Awaiting(() => store.ReleaseAsync(Scope, "v", string.Empty, ct: Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Release_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemoryReservationStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => store.ReleaseAsync(Scope, "v", "owner-a", ct: cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
