using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Subscriptions.Contracts;

/// <summary>
/// The shared, written-once contract suite for <see cref="ICheckpointSink"/> (R3 pattern,
/// TESTING-SPEC §5.1). Every backend supplies a fresh sink through <see cref="CreateSinkAsync"/>
/// and inherits these facts unchanged: the in-memory backend via
/// <see cref="InMemoryCheckpointSinkContractTests"/> now, the Postgres backend via Feature 7.
/// <para>
/// Backend-specific behavior lives outside this base: the throw-on-rollback exception <i>type</i>
/// (not frozen cross-backend — R-A) and the deferred fencing CAS (D7) are exercised in the
/// in-memory unit suite, to be ratified against Postgres in Feature 7.
/// </para>
/// </summary>
public abstract class CheckpointSinkContractTests
{
    /// <summary>Produces a fresh, empty checkpoint sink for a single test — backend-specific.</summary>
    protected abstract ValueTask<ICheckpointSink> CreateSinkAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_UnknownProjection_ReturnsNull()
    {
        var sink = await CreateSinkAsync();

        var loaded = await sink.LoadAsync("unknown", null, Ct);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsPosition()
    {
        var sink = await CreateSinkAsync();

        await sink.SaveAsync("proj", null, new GlobalPosition(42), "owner", Ct);
        var loaded = await sink.LoadAsync("proj", null, Ct);

        loaded.Should().Be(new GlobalPosition(42));
    }

    [Fact]
    public async Task Save_HigherPosition_Advances()
    {
        var sink = await CreateSinkAsync();
        await sink.SaveAsync("proj", null, new GlobalPosition(10), "owner", Ct);

        await sink.SaveAsync("proj", null, new GlobalPosition(20), "owner", Ct);

        (await sink.LoadAsync("proj", null, Ct)).Should().Be(new GlobalPosition(20));
    }

    [Fact]
    public async Task Save_SamePosition_IsIdempotent()
    {
        var sink = await CreateSinkAsync();
        await sink.SaveAsync("proj", null, new GlobalPosition(10), "owner", Ct);

        await Awaiting(() => sink.SaveAsync("proj", null, new GlobalPosition(10), "owner", Ct).AsTask())
            .Should().NotThrowAsync();

        (await sink.LoadAsync("proj", null, Ct)).Should().Be(new GlobalPosition(10));
    }

    [Fact]
    public async Task Save_DifferentProjections_AreIsolated()
    {
        var sink = await CreateSinkAsync();

        await sink.SaveAsync("proj-a", null, new GlobalPosition(5), "owner", Ct);
        await sink.SaveAsync("proj-b", null, new GlobalPosition(9), "owner", Ct);

        (await sink.LoadAsync("proj-a", null, Ct)).Should().Be(new GlobalPosition(5));
        (await sink.LoadAsync("proj-b", null, Ct)).Should().Be(new GlobalPosition(9));
    }

    [Fact]
    public async Task Save_DifferentTenants_AreIsolated()
    {
        var sink = await CreateSinkAsync();

        await sink.SaveAsync("proj", "tenant-a", new GlobalPosition(5), "owner", Ct);
        await sink.SaveAsync("proj", "tenant-b", new GlobalPosition(9), "owner", Ct);

        (await sink.LoadAsync("proj", "tenant-a", Ct)).Should().Be(new GlobalPosition(5));
        (await sink.LoadAsync("proj", "tenant-b", Ct)).Should().Be(new GlobalPosition(9));
    }

    [Fact]
    public async Task Load_NullTenant_NormalizesToSingleTenant()
    {
        var sink = await CreateSinkAsync();
        // Saved under a null tenant; loaded under "" — both must resolve to the same slot.
        await sink.SaveAsync("proj", null, new GlobalPosition(7), "owner", Ct);

        var viaEmptyString = await sink.LoadAsync("proj", "", Ct);

        viaEmptyString.Should().Be(new GlobalPosition(7));
    }
}
