using System.Text.Json;

using Acta.Abstractions;
using Acta.Aggregates;
using Acta.InMemory;
using Acta.Serialization;

namespace Acta.Tests.TestSupport;

/// <summary>
/// A snapshot-capable test aggregate (task 6.1 kit): folds the SAME <see cref="Incremented"/>/
/// <see cref="Decremented"/> events as <see cref="CounterAggregate"/> into a running counter, but
/// additionally implements <see cref="ISnapshotableAggregate"/> and captures/restores its state via
/// <see cref="System.Text.Json"/> — the shape the snapshot-first read path
/// (<c>AggregateRepository{TAggregate}</c>) exercises.
/// </summary>
public sealed class SnapshotCounter : AggregateRoot, ISnapshotableAggregate
{
    /// <summary>Current counter value, folded from <see cref="Incremented"/>/<see cref="Decremented"/>.</summary>
    public int Value { get; private set; }

    /// <inheritdoc/>
    public override int SnapshotSchemaVersion => 1;

    /// <summary>Command: records a new <see cref="Incremented"/> event.</summary>
    public void Increment() => Raise(new Incremented());

    /// <summary>Command: records a new <see cref="Decremented"/> event.</summary>
    public void Decrement() => Raise(new Decremented());

    /// <summary>
    /// Test-only escape hatch for the frozen contract's <c>protected set</c> <see cref="AggregateRoot.Id"/> —
    /// lets tests assign identity without adding a public setter to the boundary contract.
    /// </summary>
    public void AssignId(string id) => Id = id;

    /// <inheritdoc/>
    protected override void Apply(object @event)
    {
        switch (@event)
        {
            case Incremented:
                Value++;
                break;
            case Decremented:
                Value--;
                break;
            default:
                break;
        }
    }

    /// <inheritdoc/>
    protected override ReadOnlyMemory<byte>? CaptureState()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(Value));
        return bytes;
    }

    /// <inheritdoc/>
    protected override void RestoreState(ReadOnlyMemory<byte> state)
    {
        var restored = JsonSerializer.Deserialize<State>(state.Span);
        Value = restored.Value;
    }

    /// <summary>The serialized shape of <see cref="SnapshotCounter"/>'s state — bumping <see cref="SnapshotSchemaVersion"/> tracks any change here.</summary>
    private readonly record struct State(int Value);
}

/// <summary>
/// Test kit for the snapshot-first read path (task 6.1): registers <see cref="SnapshotCounter"/>'s
/// event types (shared with <see cref="CounterAggregate"/>: <see cref="Incremented"/>/
/// <see cref="Decremented"/>) in a fresh <see cref="EventTypeRegistry"/>, and supplies a one-line
/// <see cref="AggregateRepository{TAggregate}"/> builder over an <see cref="InMemoryEventStore"/>,
/// optionally wired to an <see cref="ISnapshotStore"/>.
/// </summary>
public static class SnapshotCounterEventsRegistry
{
    /// <summary>Builds a fresh <see cref="EventTypeRegistry"/> with <see cref="SnapshotCounter"/>'s two event types registered.</summary>
    /// <returns>A new registry with the two event types registered.</returns>
    public static EventTypeRegistry CreateRegistry() =>
        new EventTypeRegistry()
            .Register<Incremented>()
            .Register<Decremented>();

    /// <summary>Builds an <see cref="EventSerializer"/> over a fresh <see cref="CreateRegistry"/> and default JSON options.</summary>
    /// <returns>A new serializer bound to a fresh registry.</returns>
    public static EventSerializer CreateSerializer() => new(CreateRegistry(), JsonSerializerOptions.Default);

    /// <summary>
    /// Convenience: builds an <see cref="AggregateRepository{TAggregate}"/> for
    /// <see cref="SnapshotCounter"/>, optionally wired to <paramref name="snapshotStore"/> (task 6.1
    /// snapshot-first read path — omit to model the Tier 1 no-snapshot-store constructor, whose
    /// read path stays byte-identical to before this task).
    /// </summary>
    /// <param name="store">
    /// The store the repository reads from and appends to; defaults to a fresh
    /// <see cref="InMemoryEventStore"/> when omitted.
    /// </param>
    /// <param name="metadataFactory">
    /// The metadata factory to use; defaults to a fresh <see cref="CounterEventsRegistry.FixedMetadataFactory"/> when omitted.
    /// </param>
    /// <param name="snapshotStore">
    /// The snapshot store to inject, or <see langword="null"/> (the default) to build a repository
    /// with no snapshot store at all.
    /// </param>
    /// <returns>A new repository ready to use against <paramref name="store"/>.</returns>
    public static AggregateRepository<SnapshotCounter> CreateRepository(
        IEventStore? store = null,
        Func<EventMetadata>? metadataFactory = null,
        ISnapshotStore? snapshotStore = null) =>
        new(
            store ?? new InMemoryEventStore(),
            CreateSerializer(),
            metadataFactory ?? CounterEventsRegistry.FixedMetadataFactory(),
            snapshotStore: snapshotStore);
}

/// <summary>
/// An <see cref="IEventStore"/> decorator that records the <c>fromVersion</c> passed to the LAST
/// <see cref="ReadStreamAsync"/> call — the assertion surface the snapshot-first tests use to prove
/// the read path actually resolved <c>fromVersion</c> from a snapshot (or from 0, on rejection/
/// absence), rather than merely happening to produce the right final state. Every other member
/// delegates verbatim to <paramref name="inner"/>.
/// </summary>
/// <param name="inner">The underlying store to delegate every call to.</param>
public sealed class RecordingEventStore(IEventStore inner) : IEventStore
{
    /// <summary>The <c>fromVersion</c> argument of the most recent <see cref="ReadStreamAsync"/> call, or <see langword="null"/> if none has happened yet.</summary>
    public long? LastFromVersion { get; private set; }

    /// <inheritdoc/>
    public ValueTask<AppendResult> AppendAsync(
        string streamId, long expectedVersion, IReadOnlyList<EventData> events, CancellationToken ct = default)
        => inner.AppendAsync(streamId, expectedVersion, events, ct);

    /// <inheritdoc/>
    public IAsyncEnumerable<StoredEvent> ReadStreamAsync(
        string streamId,
        long fromVersion = 0,
        long? toVersion = null,
        Direction direction = Direction.Forwards,
        CancellationToken ct = default)
    {
        LastFromVersion = fromVersion;
        return inner.ReadStreamAsync(streamId, fromVersion, toVersion, direction, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StoredEvent> ReadAllAsync(
        GlobalPosition from,
        GlobalPosition? upTo = null,
        int? maxCount = null,
        Direction direction = Direction.Forwards,
        CancellationToken ct = default)
        => inner.ReadAllAsync(from, upTo, maxCount, direction, ct);
}
