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
