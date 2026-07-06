using Xunit;

using Acta.Abstractions;
using Acta.Aggregates;
using Acta.InMemory;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Aggregates;

/// <summary>
/// Unit tests for the snapshot-first read path in <c>AggregateRepository{TAggregate}</c> (task
/// 6.1, D2): a matching-schema snapshot restores state and folds only the tail; a rejected
/// (stale-schema) snapshot falls back to a full rebuild; a valid snapshot with an empty tail still
/// counts as "the aggregate exists" (GAP-1); the Tier 1 no-snapshot-store constructor is
/// byte-identical to before this task; a non-<see cref="ISnapshotableAggregate"/> aggregate skips
/// the snapshot-first attempt even when a store is injected; and <c>RestoreFromSnapshot</c>'s
/// fresh-aggregate guard.
/// <para>
/// <see cref="RecordingEventStore"/> (task 6.1 kit) proves WHICH <c>fromVersion</c> the read path
/// actually resolved — the equivalence assertions alone (comparing final state to a full replay)
/// would not distinguish "correctly resumed from the snapshot's tail" from "coincidentally replayed
/// everything and got the same answer".
/// </para>
/// </summary>
public sealed class AggregateRepositorySnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetByIdAsync_ValidSnapshotWithMatchingSchema_RestoresFromSnapshotAndFoldsOnlyTheTail()
    {
        var store = new InMemoryEventStore();
        var writeRepository = SnapshotCounterEventsRegistry.CreateRepository(store);
        var writer = new SnapshotCounter();
        writer.AssignId("counter-1");
        writer.Increment();
        writer.Increment();
        await writeRepository.SaveAsync(writer, ExpectedVersion.NoStream, Ct); // stream: v0,v1 — Value == 2

        // Snapshot taken exactly at the writer's current state (version 1, Value 2).
        var snapshotStore = new InMemorySnapshotStore();
        await snapshotStore.SaveAsync(
            new Snapshot("counter-1", writer.Version, writer.SnapshotSchemaVersion, writer.TakeSnapshot()!.Value, DateTimeOffset.UtcNow), Ct);

        // A genuinely new write (distinct command/repository) appends the tail event beyond the snapshot.
        var expectedVersionForTail = writer.Version;
        writer.Increment();
        var tailRepository = SnapshotCounterEventsRegistry.CreateRepository(store);
        await tailRepository.SaveAsync(writer, expectedVersionForTail, Ct); // stream: v0,v1,v2 — Value == 3

        var recordingStore = new RecordingEventStore(store);
        var snapshotRepository = SnapshotCounterEventsRegistry.CreateRepository(recordingStore, snapshotStore: snapshotStore);

        var loaded = await snapshotRepository.GetByIdAsync("counter-1", Ct);

        loaded.Should().NotBeNull();
        loaded!.Value.Should().Be(3);
        loaded.Version.Should().Be(2);
        recordingStore.LastFromVersion.Should().Be(2); // snapshot.Version (1) + 1 — only the tail is folded.

        // Equivalence (R1): a full replay with no snapshot store yields the identical state/version.
        var fullReplayRepository = SnapshotCounterEventsRegistry.CreateRepository(store);
        var fullReplay = await fullReplayRepository.GetByIdAsync("counter-1", Ct);
        fullReplay.Should().NotBeNull();
        fullReplay!.Value.Should().Be(loaded.Value);
        fullReplay.Version.Should().Be(loaded.Version);
    }

    [Fact]
    public async Task GetByIdAsync_SnapshotWithStaleSchemaVersion_FallsBackToFullRebuildFromEvents()
    {
        var store = new InMemoryEventStore();
        var writeRepository = SnapshotCounterEventsRegistry.CreateRepository(store);
        var writer = new SnapshotCounter();
        writer.AssignId("counter-1");
        writer.Increment();
        writer.Increment();
        await writeRepository.SaveAsync(writer, ExpectedVersion.NoStream, Ct); // stream: v0,v1 — Value == 2

        // Stored under a schema version different from SnapshotCounter's current SnapshotSchemaVersion (1).
        var snapshotStore = new InMemorySnapshotStore();
        await snapshotStore.SaveAsync(
            new Snapshot("counter-1", writer.Version, SchemaVersion: 0, State: new byte[] { 1 }, TakenAt: DateTimeOffset.UtcNow), Ct);

        var recordingStore = new RecordingEventStore(store);
        var snapshotRepository = SnapshotCounterEventsRegistry.CreateRepository(recordingStore, snapshotStore: snapshotStore);

        var loaded = await snapshotRepository.GetByIdAsync("counter-1", Ct);

        loaded.Should().NotBeNull();
        loaded!.Value.Should().Be(2);
        loaded.Version.Should().Be(1);
        recordingStore.LastFromVersion.Should().Be(0); // rejected snapshot -> full rebuild from version 0.
    }

    /// <summary>GAP-1: a valid snapshot whose tail is currently empty must still count as "the aggregate exists".</summary>
    [Fact]
    public async Task GetByIdAsync_ValidSnapshotWithEmptyTail_DoesNotReturnNullAndStateMatchesTheSnapshot()
    {
        var store = new InMemoryEventStore();
        var writeRepository = SnapshotCounterEventsRegistry.CreateRepository(store);
        var writer = new SnapshotCounter();
        writer.AssignId("counter-1");
        writer.Increment();
        writer.Increment();
        await writeRepository.SaveAsync(writer, ExpectedVersion.NoStream, Ct); // stream now holds EXACTLY the snapshotted events

        var snapshotStore = new InMemorySnapshotStore();
        await snapshotStore.SaveAsync(
            new Snapshot("counter-1", writer.Version, writer.SnapshotSchemaVersion, writer.TakeSnapshot()!.Value, DateTimeOffset.UtcNow), Ct);

        var snapshotRepository = SnapshotCounterEventsRegistry.CreateRepository(store, snapshotStore: snapshotStore);

        var loaded = await snapshotRepository.GetByIdAsync("counter-1", Ct);

        loaded.Should().NotBeNull();
        loaded!.Value.Should().Be(writer.Value);
        loaded.Version.Should().Be(writer.Version);
    }

    [Fact]
    public async Task GetByIdAsync_NoSnapshotStoreInjected_BehavesLikeTier1FullReplayFromVersionZero()
    {
        var store = new InMemoryEventStore();
        var writeRepository = SnapshotCounterEventsRegistry.CreateRepository(store);
        var writer = new SnapshotCounter();
        writer.AssignId("counter-1");
        writer.Increment();
        await writeRepository.SaveAsync(writer, ExpectedVersion.NoStream, Ct);

        var recordingStore = new RecordingEventStore(store);
        var readRepository = SnapshotCounterEventsRegistry.CreateRepository(recordingStore); // snapshotStore omitted -> null

        var loaded = await readRepository.GetByIdAsync("counter-1", Ct);

        loaded.Should().NotBeNull();
        recordingStore.LastFromVersion.Should().Be(0);
    }

    [Fact]
    public async Task GetByIdAsync_NonSnapshotableAggregateWithInjectedSnapshotStore_SkipsSnapshotFirstAndFullyReplays()
    {
        var store = new InMemoryEventStore();
        var writeRepository = CounterEventsRegistry.CreateRepository(store);
        var writer = new CounterAggregate();
        writer.AssignId("counter-1");
        writer.Increment();
        writer.Increment();
        await writeRepository.SaveAsync(writer, ExpectedVersion.NoStream, Ct);

        // CounterAggregate does NOT implement ISnapshotableAggregate, so the repository must never
        // even attempt a snapshot lookup for it, regardless of whether a store is injected.
        var snapshotStore = new InMemorySnapshotStore();
        var recordingStore = new RecordingEventStore(store);
        var readRepository = new AggregateRepository<CounterAggregate>(
            recordingStore,
            CounterEventsRegistry.CreateSerializer(),
            CounterEventsRegistry.FixedMetadataFactory(),
            snapshotStore: snapshotStore);

        var loaded = await readRepository.GetByIdAsync("counter-1", Ct);

        loaded.Should().NotBeNull();
        loaded!.Applied.Should().Be(2);
        recordingStore.LastFromVersion.Should().Be(0);
    }

    [Fact]
    public void RestoreFromSnapshot_AggregateAlreadyAtNonInitialVersion_ThrowsInvalidOperationException()
    {
        var aggregate = new SnapshotCounter();
        aggregate.Increment(); // Version becomes 0 — no longer the fresh -1 RestoreFromSnapshot requires.

        Invoking(() => aggregate.RestoreFromSnapshot(new byte[] { 1 }, 0)).Should().Throw<InvalidOperationException>();
    }
}
