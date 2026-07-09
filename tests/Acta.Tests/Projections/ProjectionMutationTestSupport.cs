using System.Runtime.CompilerServices;

using Acta.Abstractions;

// Lane-local test doubles for the Projections mutation-kill suite (ProjectionDaemonMutationTests,
// InlineProjectionRunnerMutationTests). Deliberately separate from the shared Acta.Tests.TestSupport
// helpers (which sibling lanes also depend on) so this lane's additions never risk a merge conflict
// with concurrent work in other lanes.

namespace Acta.Tests.Projections;

/// <summary>
/// An <see cref="ICheckpointSink"/> decorator counting how many times <see cref="LoadAsync"/> and
/// <see cref="SaveAsync"/> were invoked, forwarding every call to <paramref name="inner"/> — used to
/// observe the daemon's "trust the cached checkpoint" (load) and "only save when the checkpoint
/// actually advanced" (save) invariants directly, rather than only their end-to-end effects.
/// </summary>
/// <param name="inner">The real sink to forward every call to.</param>
public sealed class CountingCheckpointSink(ICheckpointSink inner) : ICheckpointSink
{
    private readonly ICheckpointSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The number of <see cref="LoadAsync"/> calls observed so far.</summary>
    public int LoadCallCount { get; private set; }

    /// <summary>The number of <see cref="SaveAsync"/> calls observed so far.</summary>
    public int SaveCallCount { get; private set; }

    /// <inheritdoc/>
    public ValueTask<GlobalPosition?> LoadAsync(string projectionName, string? tenantId, CancellationToken ct = default)
    {
        LoadCallCount++;
        return _inner.LoadAsync(projectionName, tenantId, ct);
    }

    /// <inheritdoc/>
    public ValueTask SaveAsync(string projectionName, string? tenantId, GlobalPosition position, string ownerToken, CancellationToken ct = default)
    {
        SaveCallCount++;
        return _inner.SaveAsync(projectionName, tenantId, position, ownerToken, ct);
    }
}

/// <summary>
/// An <see cref="ICheckpointSink"/> decorator that forwards to <paramref name="inner"/> and then
/// cancels <paramref name="cts"/> — used to end a daemon tick's <c>while</c> loop gracefully
/// (no exception; the next loop-top cancellation check simply exits) right after a checkpoint save
/// completes, isolating "what happens between the save and the next batch read" from any subsequent
/// iteration's own state changes.
/// </summary>
/// <param name="inner">The real sink to forward every call to.</param>
/// <param name="cts">Cancelled immediately after <paramref name="inner"/>'s <see cref="SaveAsync"/> completes.</param>
public sealed class CancelAfterSaveCheckpointSink(ICheckpointSink inner, CancellationTokenSource cts) : ICheckpointSink
{
    private readonly ICheckpointSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly CancellationTokenSource _cts = cts ?? throw new ArgumentNullException(nameof(cts));

    /// <inheritdoc/>
    public ValueTask<GlobalPosition?> LoadAsync(string projectionName, string? tenantId, CancellationToken ct = default)
        => _inner.LoadAsync(projectionName, tenantId, ct);

    /// <inheritdoc/>
    public async ValueTask SaveAsync(string projectionName, string? tenantId, GlobalPosition position, string ownerToken, CancellationToken ct = default)
    {
        await _inner.SaveAsync(projectionName, tenantId, position, ownerToken, ct);
        _cts.Cancel();
    }
}

/// <summary>
/// A fully-fake <see cref="ISubscriptionSource"/> whose <see cref="ReadBatchAsync"/> always returns an
/// empty batch (forcing the daemon's gap-detection raw-peek branch) while <see cref="ReadFromAsync"/>
/// yields every event in <paramref name="rawEvents"/>, counting exactly how many were pulled by the
/// consumer via <see cref="YieldedCount"/> — used to prove the daemon's raw peek stops after the
/// FIRST raw event (existence, not content, is all the guard needs) rather than draining the whole
/// stream.
/// </summary>
/// <param name="rawEvents">The raw events to yield, in order, above whatever checkpoint is requested.</param>
public sealed class CountingRawPeekSource(IReadOnlyList<StoredEvent> rawEvents) : ISubscriptionSource
{
    private readonly IReadOnlyList<StoredEvent> _rawEvents = rawEvents ?? throw new ArgumentNullException(nameof(rawEvents));

    /// <summary>How many of <see cref="_rawEvents"/> the consumer actually pulled before stopping.</summary>
    public int YieldedCount { get; private set; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StoredEvent> ReadFromAsync(GlobalPosition from, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask; // Synchronous by design (a test double) — silences CS1998.

        foreach (var stored in _rawEvents)
        {
            YieldedCount++;
            yield return stored;
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<StoredEvent>> ReadBatchAsync(
        GlobalPosition from, int maxCount, IReadOnlySet<string>? eventTypes = null, CancellationToken ct = default)
        => new(Array.Empty<StoredEvent>());

    /// <summary>Builds a synthetic, minimal <see cref="StoredEvent"/> at <paramref name="globalPosition"/> — content is irrelevant, only its existence is observed.</summary>
    public static StoredEvent SyntheticEvent(long globalPosition) => new(
        Guid.NewGuid(),
        StreamId: "raw-peek-stream",
        Version: 0,
        GlobalPosition: new GlobalPosition(globalPosition),
        EventType: "SyntheticRawEvent",
        SchemaVersion: 1,
        Payload: ReadOnlyMemory<byte>.Empty,
        Metadata: new EventMetadata { MessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), CausationId = Guid.NewGuid() },
        Timestamp: DateTimeOffset.UtcNow);
}
