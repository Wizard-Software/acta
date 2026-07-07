using Xunit;

using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Snapshots;

/// <summary>
/// Unit tests for <see cref="InMemorySnapshotStore"/> (task 6.1, D4): absent-entry/round-trip
/// behavior, schema-mismatch rejection in BOTH directions (ADR-006 Enforcement MUST — <c>!=</c>,
/// decision OQ-2), CAS-by-version semantics, invalidation via <see cref="ISnapshotStore.DeleteAsync"/>,
/// and cancellation on every method.
/// </summary>
public sealed class InMemorySnapshotStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Snapshot CreateSnapshot(
        string streamId = "stream-1",
        long version = 0,
        int schemaVersion = 1,
        byte[]? state = null,
        DateTimeOffset? takenAt = null) =>
        new(streamId, version, schemaVersion, state ?? [1, 2, 3], takenAt ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task LoadAsync_NoStoredSnapshot_ReturnsNull()
    {
        var store = new InMemorySnapshotStore();

        var loaded = await store.LoadAsync("stream-ghost", expectedSchemaVersion: 1, Ct);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_MatchingSchemaAfterSave_ReturnsTheSavedSnapshot()
    {
        var store = new InMemorySnapshotStore();
        var takenAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot = CreateSnapshot(version: 3, schemaVersion: 2, state: [9, 8, 7], takenAt: takenAt);
        await store.SaveAsync(snapshot, Ct);

        var loaded = await store.LoadAsync(snapshot.StreamId, expectedSchemaVersion: 2, Ct);

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(3);
        loaded.SchemaVersion.Should().Be(2);
        loaded.State.ToArray().Should().Equal(9, 8, 7);
        loaded.TakenAt.Should().Be(takenAt);
    }

    [Fact]
    public async Task LoadAsync_OlderStoredSchemaThanExpected_ReturnsNull()
    {
        var store = new InMemorySnapshotStore();
        var snapshot = CreateSnapshot(schemaVersion: 1);
        await store.SaveAsync(snapshot, Ct);

        var loaded = await store.LoadAsync(snapshot.StreamId, expectedSchemaVersion: 2, Ct);

        loaded.Should().BeNull();
    }

    /// <summary>
    /// OQ-2 edge case: a stored schema NEWER than what the caller expects is rejected exactly like
    /// an older one — the comparison is <c>!=</c>, never <c>&lt;</c> (ADR-006 Enforcement MUST).
    /// Accepting a newer/unknown schema would let a state shape the caller's code does not
    /// understand be folded in silently (SEC-1).
    /// </summary>
    [Fact]
    public async Task LoadAsync_NewerStoredSchemaThanExpected_ReturnsNull()
    {
        var store = new InMemorySnapshotStore();
        var snapshot = CreateSnapshot(schemaVersion: 5);
        await store.SaveAsync(snapshot, Ct);

        var loaded = await store.LoadAsync(snapshot.StreamId, expectedSchemaVersion: 2, Ct);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_LowerOrEqualVersionThanExisting_IsIgnoredAndExistingSnapshotIsRetained()
    {
        var store = new InMemorySnapshotStore();
        var original = CreateSnapshot(version: 5, state: [1]);
        await store.SaveAsync(original, Ct);

        await store.SaveAsync(CreateSnapshot(version: 5, state: [2]), Ct); // equal version -> ignored
        await store.SaveAsync(CreateSnapshot(version: 3, state: [3]), Ct); // lower version -> ignored

        var loaded = await store.LoadAsync(original.StreamId, expectedSchemaVersion: 1, Ct);

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(5);
        loaded.State.ToArray().Should().Equal(1);
    }

    [Fact]
    public async Task SaveAsync_HigherVersionThanExisting_Overwrites()
    {
        var store = new InMemorySnapshotStore();
        await store.SaveAsync(CreateSnapshot(version: 1, state: [1]), Ct);

        await store.SaveAsync(CreateSnapshot(version: 2, state: [2]), Ct);

        var loaded = await store.LoadAsync("stream-1", expectedSchemaVersion: 1, Ct);

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(2);
        loaded.State.ToArray().Should().Equal(2);
    }

    [Fact]
    public async Task SaveAsync_ConcurrentSavesAtDifferentVersions_RetainsTheMaximumVersion()
    {
        var store = new InMemorySnapshotStore();
        var snapshots = Enumerable.Range(0, 20).Select(i => CreateSnapshot(version: i, state: [(byte)i])).ToArray();

        await Task.WhenAll(snapshots.Select(s => store.SaveAsync(s, Ct).AsTask()));

        var loaded = await store.LoadAsync("stream-1", expectedSchemaVersion: 1, Ct);

        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(19);
    }

    [Fact]
    public async Task DeleteAsync_ThenLoadAsync_ReturnsNull()
    {
        var store = new InMemorySnapshotStore();
        var snapshot = CreateSnapshot();
        await store.SaveAsync(snapshot, Ct);

        await store.DeleteAsync(snapshot.StreamId, Ct);
        var loaded = await store.LoadAsync(snapshot.StreamId, expectedSchemaVersion: 1, Ct);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NoStoredSnapshot_IsAnIdempotentNoOp()
    {
        var store = new InMemorySnapshotStore();

        var exception = await Record.ExceptionAsync(async () => await store.DeleteAsync("stream-ghost", Ct));

        exception.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemorySnapshotStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => store.LoadAsync("stream-1", 1, cts.Token).AsTask()).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SaveAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemorySnapshotStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => store.SaveAsync(CreateSnapshot(), cts.Token).AsTask()).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DeleteAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var store = new InMemorySnapshotStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => store.DeleteAsync("stream-1", cts.Token).AsTask()).Should().ThrowAsync<OperationCanceledException>();
    }
}
