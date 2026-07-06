namespace Acta.Abstractions;

/// <summary>
/// Base class for event-sourced aggregate roots (FR-2/FR-11). State is rebuilt by folding a
/// stream of events; new decisions are recorded by <see cref="Raise"/>.
/// <para>
/// Totality-of-<see cref="Apply"/> invariant (AK-4): the state mutator NEVER throws and performs
/// no I/O — an unknown event type is a no-op, not a fault. All validation belongs to the command
/// phase (the concrete command method, before it calls <see cref="Raise"/>). Enforced by a
/// property test, not by the compiler.
/// </para>
/// </summary>
public abstract class AggregateRoot
{
    private readonly List<object> _uncommittedEvents = [];

    /// <summary>Aggregate (stream) identity.</summary>
    public string Id { get; protected set; } = default!;

    /// <summary>0-based version of the last applied event; -1 when no events have been applied.</summary>
    public long Version { get; private set; } = -1;

    /// <summary>Events raised since the last <see cref="ClearUncommittedEvents"/> — pending persistence.</summary>
    public IReadOnlyList<object> UncommittedEvents => _uncommittedEvents;

    /// <summary>
    /// Version of this aggregate's STATE SHAPE (not the stream version, and unrelated to
    /// <see cref="Version"/>). Incrementing this value in the SAME PR as any change to the shape
    /// returned by <see cref="CaptureState"/> is the enforcement mechanism behind ADR-006's
    /// "reject on any schema mismatch" rule (task 6.1 decision OQ-2): a stale or unknown stored
    /// schema is rejected by <c>ISnapshotStore.LoadAsync</c> before it ever reaches
    /// <see cref="RestoreState"/>. Defaults to 0 for aggregates that do not override it.
    /// </summary>
    public virtual int SnapshotSchemaVersion => 0;

    /// <summary>
    /// Command-side: validate BEFORE calling this, then record a new event (applies state,
    /// advances <see cref="Version"/>, and queues it as uncommitted).
    /// </summary>
    /// <param name="event">The event to record. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="event"/> is <see langword="null"/>.</exception>
    protected void Raise(object @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ApplyAndAdvance(@event);
        _uncommittedEvents.Add(@event);
    }

    /// <summary>
    /// Total state mutator: MUST NOT throw and MUST NOT perform I/O; an unrecognized event type
    /// must be a no-op (FR-11, AK-4). Implementations typically <see langword="switch"/> on the
    /// event's runtime type, with a <see langword="default"/> branch that does nothing.
    /// </summary>
    /// <param name="event">The event to fold into the aggregate's state.</param>
    protected abstract void Apply(object @event);

    /// <summary>
    /// Captures the current state as opaque bytes for a snapshot, or <see langword="null"/> when
    /// this aggregate does not support snapshotting (the default — preserving today's
    /// full-replay-only behavior for every aggregate that does not override it). Aggregates opt in
    /// by overriding this method AND implementing <c>ISnapshotableAggregate</c> — the marker the
    /// read path checks (a zero-allocation <see langword="is"/> check) to decide whether to attempt
    /// a snapshot-first load; it never calls this method purely to probe for support (task 6.1
    /// decision OQ-1).
    /// </summary>
    /// <returns>The serialized state, or <see langword="null"/> when snapshotting is unsupported.</returns>
    protected virtual ReadOnlyMemory<byte>? CaptureState() => null;

    /// <summary>
    /// Restores state from a previously captured snapshot. MUST be total — no I/O, and MUST NOT
    /// throw when invoked with bytes captured at a matching <see cref="SnapshotSchemaVersion"/>
    /// (AK-4, mirroring <see cref="Apply"/>'s totality contract). Callers only ever invoke this
    /// after <c>ISnapshotStore.LoadAsync</c> has already accepted the snapshot's schema version as
    /// compatible — corrupted bytes at an otherwise-matching schema are out of scope for the
    /// in-memory backend (a documented prerequisite for a durable backend in Feature 7).
    /// </summary>
    /// <param name="state">The previously captured state to restore.</param>
    protected virtual void RestoreState(ReadOnlyMemory<byte> state)
    {
    }

    /// <summary>
    /// Rehydrate state from already-persisted events, in stream order. Does NOT enqueue
    /// uncommitted events — rehydration replays history, it does not create new decisions.
    /// </summary>
    /// <param name="history">The events to fold, oldest first. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> is <see langword="null"/>.</exception>
    public void LoadFromHistory(IEnumerable<object> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        foreach (var @event in history)
        {
            ApplyAndAdvance(@event);
        }
    }

    /// <summary>
    /// Repository-facing facade: captures this aggregate's state for a snapshot, or
    /// <see langword="null"/> when snapshotting is unsupported. Delegates to
    /// <see cref="CaptureState"/> — kept <see langword="public"/>, consistent with the
    /// already-public <see cref="LoadFromHistory"/>, so the core repository can call it without an
    /// <c>InternalsVisibleTo</c> grant across the assembly boundary.
    /// </summary>
    /// <returns>The serialized state, or <see langword="null"/> when snapshotting is unsupported.</returns>
    public ReadOnlyMemory<byte>? TakeSnapshot() => CaptureState();

    /// <summary>
    /// Repository-facing facade: restores state from a snapshot and sets <see cref="Version"/> to
    /// <paramref name="version"/> — the ONLY mutation path for <see cref="Version"/> besides the
    /// shared <c>ApplyAndAdvance</c> used by <see cref="Raise"/>/<see cref="LoadFromHistory"/>.
    /// Allowed exclusively on a fresh aggregate (<see cref="Version"/> == -1): calling it on an
    /// already-hydrated aggregate would silently discard whatever history it already folded, so it
    /// throws instead.
    /// </summary>
    /// <param name="state">The snapshot's captured state, folded in via <see cref="RestoreState"/>.</param>
    /// <param name="version">The stream version the snapshot is current as of.</param>
    /// <exception cref="InvalidOperationException"><see cref="Version"/> is not -1 (this aggregate already has history).</exception>
    public void RestoreFromSnapshot(ReadOnlyMemory<byte> state, long version)
    {
        if (Version != -1)
        {
            throw new InvalidOperationException(
                $"RestoreFromSnapshot is only allowed on a fresh aggregate (Version == -1); this aggregate is already at version {Version}.");
        }

        RestoreState(state);
        Version = version;
    }

    /// <summary>Clear the uncommitted queue after a successful append. Does not reset <see cref="Version"/>.</summary>
    public void ClearUncommittedEvents() => _uncommittedEvents.Clear();

    /// <summary>Folds a single event into state and advances <see cref="Version"/> by one — the
    /// single path shared by <see cref="Raise"/> and <see cref="LoadFromHistory"/> so both
    /// guarantee identical version progress.</summary>
    private void ApplyAndAdvance(object @event)
    {
        Apply(@event);
        Version++;
    }
}
