using Xunit;

using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Subscriptions;

/// <summary>
/// Backend-specific unit tests for <see cref="InMemoryCheckpointSink"/> — the monotonic
/// throw-on-rollback default (exception type not frozen cross-backend, R-A), argument guards, the
/// deliberately non-fenced <c>ownerToken</c> (D7/D11), and the atomic monotonic guard under
/// concurrency (D10).
/// </summary>
public sealed class InMemoryCheckpointSinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Save_LowerPosition_ThrowsInvalidOperationExceptionAndKeepsCurrent()
    {
        var sink = new InMemoryCheckpointSink();
        await sink.SaveAsync("proj", null, new GlobalPosition(10), "owner", Ct);

        await Awaiting(() => sink.SaveAsync("proj", null, new GlobalPosition(5), "owner", Ct).AsTask())
            .Should().ThrowAsync<InvalidOperationException>();

        // The rejected rollback must not have mutated the stored checkpoint.
        (await sink.LoadAsync("proj", null, Ct)).Should().Be(new GlobalPosition(10));
    }

    [Fact]
    public async Task Save_NullProjectionName_ThrowsArgumentException()
    {
        var sink = new InMemoryCheckpointSink();

        await Awaiting(() => sink.SaveAsync(null!, null, new GlobalPosition(1), "owner", Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Save_EmptyProjectionName_ThrowsArgumentException()
    {
        var sink = new InMemoryCheckpointSink();

        await Awaiting(() => sink.SaveAsync(string.Empty, null, new GlobalPosition(1), "owner", Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Save_NullOwnerToken_ThrowsArgumentException()
    {
        var sink = new InMemoryCheckpointSink();

        await Awaiting(() => sink.SaveAsync("proj", null, new GlobalPosition(1), null!, Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Save_EmptyOwnerToken_ThrowsArgumentException()
    {
        var sink = new InMemoryCheckpointSink();

        await Awaiting(() => sink.SaveAsync("proj", null, new GlobalPosition(1), string.Empty, Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Load_NullProjectionName_ThrowsArgumentException()
    {
        var sink = new InMemoryCheckpointSink();

        await Awaiting(() => sink.LoadAsync(null!, null, Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Save_CancelledToken_ThrowsOperationCanceledException()
    {
        var sink = new InMemoryCheckpointSink();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => sink.SaveAsync("proj", null, new GlobalPosition(1), "owner", cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Save_WithDifferentOwnerToken_DoesNotFence()
    {
        // D11 (documenting): the Tier-1 in-memory sink accepts a save from a different owner token
        // WITHOUT throwing CheckpointFencedException — single-process has no leadership to fence
        // (D7). This positive test prevents a false sense that fencing works on any ICheckpointSink.
        var sink = new InMemoryCheckpointSink();
        await sink.SaveAsync("proj", null, new GlobalPosition(5), "owner-A", Ct);

        await Awaiting(() => sink.SaveAsync("proj", null, new GlobalPosition(10), "owner-B", Ct).AsTask())
            .Should().NotThrowAsync();

        (await sink.LoadAsync("proj", null, Ct)).Should().Be(new GlobalPosition(10));
    }

    [Fact]
    public async Task Save_ConcurrentMonotonicWrites_ConvergeToMaxWithoutLostUpdate()
    {
        // D10: the monotonic guard must be atomic. If it were a TryGetValue-then-write span, a
        // lower position could overwrite a concurrently-advanced one (lost update) and the final
        // checkpoint would be below the true max. Concurrent lower saves legitimately throw the
        // no-rollback guard — that is the contract, not a failure — so they are swallowed here.
        var sink = new InMemoryCheckpointSink();
        const int writers = 64;
        var ct = Ct;

        var tasks = Enumerable.Range(1, writers).Select(i => Task.Run(async () =>
        {
            try
            {
                await sink.SaveAsync("proj", null, new GlobalPosition(i), "owner", ct);
            }
            catch (InvalidOperationException)
            {
                // A save lower than one a concurrent writer already committed — rejected by the
                // monotonic "no rollback" guard. Expected under the race, not an error.
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        (await sink.LoadAsync("proj", null, ct)).Should().Be(new GlobalPosition(writers));
    }
}
