using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local implementation of <see cref="IEventAppendTransaction"/> — the unit of
/// atomicity behind the AK-1 single-commit seam (ADR-002, FR-14, D-8.3-A).
/// <para>
/// <b>Buffering:</b> <see cref="AppendAsync"/> validates its <c>expectedVersion</c> guard
/// optimistically against the shared <see cref="InMemoryOutboxState"/>'s committed snapshot
/// combined with this transaction's own not-yet-committed appends, computes the resulting
/// <see cref="AppendResult"/>, and buffers the request — nothing becomes visible to readers yet.
/// <see cref="EnlistOutbox"/> (called by <see cref="InMemoryOutboxFlush"/>) similarly buffers
/// integration events. <see cref="CommitAsync"/> re-validates every buffered append against the
/// freshest committed state and publishes both the appends and the outbox entries in one atomic
/// swap (<see cref="InMemoryOutboxState.Commit"/>) — all-or-nothing. Disposing without committing
/// discards the buffers: nothing is ever published (rollback).
/// </para>
/// </summary>
/// <param name="outboxState">The shared committed state this transaction publishes to on commit.</param>
/// <param name="timeProvider">Clock used to stamp <see cref="StoredEvent.Timestamp"/> at commit time.</param>
public sealed class InMemoryEventAppendTransaction(InMemoryOutboxState outboxState, TimeProvider timeProvider)
    : IEventAppendTransaction
{
    private readonly Lock _gate = new();
    private readonly List<PendingAppend> _pendingAppends = [];
    private readonly List<CollectedIntegrationEvent> _pendingOutbox = [];
    private readonly Dictionary<string, long> _localStreamVersion = [];
    private readonly Dictionary<string, GlobalPosition> _localStreamHeadPosition = [];
    private long _localNextGlobalPosition = outboxState.NextGlobalPosition;
    private bool _committed;
    private bool _disposed;

    /// <inheritdoc/>
    public ValueTask<AppendResult> AppendAsync(
        string streamId,
        long expectedVersion,
        IReadOnlyList<EventData> events,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);
        ArgumentNullException.ThrowIfNull(events);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfCommitted("append to");

            var (currentLastVersion, currentHeadPosition) = StreamHead(streamId);

            if (events.Count == 0)
            {
                return ValueTask.FromResult(new AppendResult(currentLastVersion, currentHeadPosition, Deduplicated: false));
            }

            InMemoryOutboxState.ValidateExpectedVersion(streamId, expectedVersion, currentLastVersion);

            var lastVersion = currentLastVersion + events.Count;
            var lastPosition = new GlobalPosition(_localNextGlobalPosition + events.Count - 1);

            _pendingAppends.Add(new PendingAppend(streamId, expectedVersion, events));
            _localStreamVersion[streamId] = lastVersion;
            _localStreamHeadPosition[streamId] = lastPosition;
            _localNextGlobalPosition += events.Count;

            return ValueTask.FromResult(new AppendResult(lastVersion, lastPosition, Deduplicated: false));
        }
    }

    /// <inheritdoc/>
    public ValueTask CommitAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfCommitted("commit");

            outboxState.Commit(_pendingAppends, _pendingOutbox, timeProvider);
            _committed = true;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;

            if (!_committed)
            {
                // Rollback: discard everything buffered so far — nothing becomes visible.
                _pendingAppends.Clear();
                _pendingOutbox.Clear();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Buffers <paramref name="events"/> as pending outbox entries, enlisted by
    /// <see cref="InMemoryOutboxFlush"/> into this same transaction. Published atomically with
    /// this transaction's appends when <see cref="CommitAsync"/> is called.
    /// </summary>
    /// <param name="events">The integration events drained from the collector.</param>
    internal void EnlistOutbox(IReadOnlyList<CollectedIntegrationEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfCommitted("enlist outbox entries into");

            _pendingOutbox.AddRange(events);
        }
    }

    private (long LastVersion, GlobalPosition HeadPosition) StreamHead(string streamId)
    {
        if (_localStreamVersion.TryGetValue(streamId, out var version))
        {
            return (version, _localStreamHeadPosition[streamId]);
        }

        var committed = outboxState.ReadStream(streamId);
        return committed.Count > 0
            ? (committed[^1].Version, committed[^1].GlobalPosition)
            : (-1L, GlobalPosition.Start);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfCommitted(string action)
    {
        if (_committed)
        {
            throw new InvalidOperationException($"Cannot {action} a transaction that has already been committed.");
        }
    }

    /// <summary>A buffered, not-yet-committed append request within this transaction.</summary>
    /// <param name="StreamId">Identifier of the target stream.</param>
    /// <param name="ExpectedVersion">The optimistic-concurrency guard, as originally supplied by the caller.</param>
    /// <param name="Events">The events to append, in order.</param>
    internal readonly record struct PendingAppend(string StreamId, long ExpectedVersion, IReadOnlyList<EventData> Events);
}
