using Acta.Abstractions;

namespace Acta.Upcasting;

/// <summary>
/// A chain of <see cref="IEventUpcaster"/> steps indexed by <c>(EventType, FromSchemaVersion)</c>,
/// performing the on-read conversion walk described by Grupa 7 / FR-8: starting from a stored
/// event's <c>(EventType, SchemaVersion)</c>, repeatedly apply the upcaster registered for the
/// current key — following cross-type conversions (the walk continues keyed on the produced
/// event type) and 1:N fan-out (every produced event continues the walk independently) — until no
/// upcaster is registered for the current key, at which point that event is terminal ("already at
/// its current schema version").
/// <para>
/// <b>Immutable after construction, lock-free concurrent reads, no shared mutable state.</b> The
/// index is built once in the constructor and never mutated afterward; <see cref="Upcast"/> and
/// <see cref="UpcastRaw"/> only ever read it, so concurrent calls from multiple threads are safe
/// without any locking — the same behavior class as <c>EventTypeRegistry</c> after host-startup
/// registration.
/// </para>
/// <para>
/// <b>No I/O, no reflection on the walk.</b> The chain itself never performs I/O and never uses
/// reflection while walking (only dictionary lookups and interface dispatch, built from the
/// pre-resolved index) — see 05-implementation §2. Whether an individual <see cref="IEventUpcaster"/>
/// honors its own "no I/O" contract is outside this type's control (SEC-3) and is enforced by code
/// review, not by this chain.
/// </para>
/// <para>
/// <b>Fail-fast construction.</b> Two upcasters cannot claim the same
/// <c>(EventType, FromSchemaVersion)</c> key (an ambiguous chain) and the exact same upcaster
/// instance cannot be registered twice for the same key (an unconditional self-loop) — both throw
/// <see cref="UpcasterCycleException"/> from the constructor.
/// </para>
/// <para>
/// <b>Resource-exhaustion guards.</b> Three independent guards protect a walk from an unbounded or
/// exponential blow-up: (1) a per-path visited-set catches an operational cycle (a path that
/// revisits a schema it has already seen — the only way a cross-type cycle can be detected, since
/// the port does not declare a target ahead of time); (2) a per-branch hard iteration limit
/// (<c>Count + 1</c>) is a defense-in-depth backstop against divergence; and (3) a global
/// per-invocation work budget (total queue iterations plus terminal events emitted, across every
/// branch of one call) guards against a combinatorial "diamond lattice" of 1:N upcasters, where
/// paths reconverge on shared downstream schemas without any single path repeating a key — the
/// per-path guard alone cannot see this, since no individual path is cyclic. All three throw
/// <see cref="UpcasterCycleException"/>.
/// </para>
/// </summary>
public sealed class UpcasterChain
{
    /// <summary>The approximate global work budget granted per registered upcaster (see <see cref="ComputeWorkBudget"/>).</summary>
    private const int WorkBudgetPerUpcaster = 64;

    private readonly Dictionary<(string EventType, int FromSchemaVersion), IEventUpcaster> _index;

    /// <summary>
    /// Builds the chain's index from <paramref name="upcasters"/>, validating it fail-fast.
    /// </summary>
    /// <param name="upcasters">The upcasters to register, each contributing exactly one key (its own <see cref="IEventUpcaster.EventType"/>/<see cref="IEventUpcaster.FromSchemaVersion"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="upcasters"/> or one of its elements is <see langword="null"/>.</exception>
    /// <exception cref="UpcasterCycleException">
    /// Two distinct upcasters are registered for the same <c>(EventType, FromSchemaVersion)</c>
    /// key (ambiguous chain), or the same upcaster instance is registered more than once for the
    /// same key (an unconditional self-loop).
    /// </exception>
    public UpcasterChain(IEnumerable<IEventUpcaster> upcasters)
    {
        ArgumentNullException.ThrowIfNull(upcasters);

        var index = new Dictionary<(string EventType, int FromSchemaVersion), IEventUpcaster>();
        foreach (var upcaster in upcasters)
        {
            ArgumentNullException.ThrowIfNull(upcaster);

            var key = (upcaster.EventType, upcaster.FromSchemaVersion);
            if (index.TryGetValue(key, out var existing))
            {
                throw ReferenceEquals(existing, upcaster)
                    ? UpcasterCycleException.ForSelfReference(key.EventType, key.FromSchemaVersion)
                    : UpcasterCycleException.ForDuplicateKey(key.EventType, key.FromSchemaVersion);
            }

            index.Add(key, upcaster);
        }

        _index = index;
    }

    /// <summary>The number of registered upcasters.</summary>
    public int Count => _index.Count;

    /// <summary>
    /// Walks the chain for a deserialized event, following cross-type conversions and 1:N
    /// fan-out until every produced event is terminal (no upcaster registered for its key).
    /// </summary>
    /// <param name="event">The source event instance, already deserialized to its stored schema version.</param>
    /// <param name="eventType">The source event's logical event-type name.</param>
    /// <param name="schemaVersion">The source event's stored schema version.</param>
    /// <param name="metadata">The causation metadata associated with the source event.</param>
    /// <returns>
    /// The terminal event(s) reached by the walk. If no upcaster is registered for
    /// <paramref name="eventType"/>/<paramref name="schemaVersion"/>, this is a single-element
    /// result carrying <paramref name="event"/> unchanged (the same reference) — "already at its
    /// current schema version".
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="event"/> or <paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="UpcasterCycleException">
    /// The walk detected an operational cycle, a single branch exceeded its hard iteration limit,
    /// or the global per-invocation work budget was exhausted.
    /// </exception>
    public IReadOnlyList<UpcastedEvent> Upcast(object @event, string eventType, int schemaVersion, EventMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(metadata);

        if (!_index.TryGetValue((eventType, schemaVersion), out _))
        {
            // PERF-2 fast path: the input is already terminal — skip building any walk state.
            return [new UpcastedEvent(@event, eventType, schemaVersion)];
        }

        var results = new List<UpcastedEvent>();
        var queue = new Queue<WalkNode<object>>();
        var depthLimit = Count + 1;
        var workBudget = ComputeWorkBudget();
        var workDone = 0;

        queue.Enqueue(new WalkNode<object>(@event, new PathNode(eventType, schemaVersion, Parent: null), Depth: 0));

        while (queue.TryDequeue(out var node))
        {
            if (++workDone > workBudget)
                throw UpcasterCycleException.ForWorkBudgetExceeded(node.Key.EventType, node.Key.SchemaVersion, workBudget, BuildPath(node.Key));

            if (!_index.TryGetValue((node.Key.EventType, node.Key.SchemaVersion), out var upcaster))
            {
                results.Add(new UpcastedEvent(node.Payload, node.Key.EventType, node.Key.SchemaVersion));
                continue;
            }

            if (node.Depth >= depthLimit)
                throw UpcasterCycleException.ForBranchDepthExceeded(node.Key.EventType, node.Key.SchemaVersion, depthLimit, BuildPath(node.Key));

            foreach (var produced in upcaster.Upcast(node.Payload, metadata))
            {
                if (PathContains(node.Key, produced.EventType, produced.SchemaVersion))
                {
                    var cyclePath = BuildPath(node.Key);
                    cyclePath.Add((produced.EventType, produced.SchemaVersion));
                    throw UpcasterCycleException.ForCycle(produced.EventType, produced.SchemaVersion, cyclePath);
                }

                var childKey = new PathNode(produced.EventType, produced.SchemaVersion, node.Key);
                queue.Enqueue(new WalkNode<object>(produced.Event, childKey, node.Depth + 1));
            }
        }

        return results;
    }

    /// <summary>
    /// Walks the chain for a raw JSON payload, following cross-type conversions and 1:N fan-out
    /// until every produced payload is terminal (no upcaster registered for its key). Homogeneous
    /// with <see cref="IRawJsonEventUpcaster"/> steps only (D3): every non-terminal step visited
    /// must implement <see cref="IRawJsonEventUpcaster"/>.
    /// </summary>
    /// <param name="eventType">The source payload's logical event-type name.</param>
    /// <param name="schemaVersion">The source payload's stored schema version.</param>
    /// <param name="payloadJson">The source payload's raw, serialized JSON bytes.</param>
    /// <param name="metadata">The causation metadata associated with the source payload.</param>
    /// <returns>
    /// The terminal payload(s) reached by the walk. If no upcaster is registered for
    /// <paramref name="eventType"/>/<paramref name="schemaVersion"/>, this is a single-element
    /// result carrying <paramref name="payloadJson"/> unchanged — "already at its current schema
    /// version".
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A non-terminal step's registered upcaster does not implement <see cref="IRawJsonEventUpcaster"/>.
    /// </exception>
    /// <exception cref="UpcasterCycleException">
    /// The walk detected an operational cycle, a single branch exceeded its hard iteration limit,
    /// or the global per-invocation work budget was exhausted.
    /// </exception>
    public IReadOnlyList<RawUpcastedEvent> UpcastRaw(string eventType, int schemaVersion, ReadOnlyMemory<byte> payloadJson, EventMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!_index.TryGetValue((eventType, schemaVersion), out _))
        {
            // PERF-2 fast path: the input is already terminal — skip building any walk state.
            return [new RawUpcastedEvent(payloadJson, eventType, schemaVersion)];
        }

        var results = new List<RawUpcastedEvent>();
        var queue = new Queue<WalkNode<ReadOnlyMemory<byte>>>();
        var depthLimit = Count + 1;
        var workBudget = ComputeWorkBudget();
        var workDone = 0;

        queue.Enqueue(new WalkNode<ReadOnlyMemory<byte>>(payloadJson, new PathNode(eventType, schemaVersion, Parent: null), Depth: 0));

        while (queue.TryDequeue(out var node))
        {
            if (++workDone > workBudget)
                throw UpcasterCycleException.ForWorkBudgetExceeded(node.Key.EventType, node.Key.SchemaVersion, workBudget, BuildPath(node.Key));

            if (!_index.TryGetValue((node.Key.EventType, node.Key.SchemaVersion), out var upcaster))
            {
                results.Add(new RawUpcastedEvent(node.Payload, node.Key.EventType, node.Key.SchemaVersion));
                continue;
            }

            if (upcaster is not IRawJsonEventUpcaster rawUpcaster)
                throw new InvalidOperationException(
                    $"Upcaster chain homogeneity violation: the upcaster registered for event type " +
                    $"'{node.Key.EventType}' at schema version {node.Key.SchemaVersion} does not implement " +
                    $"{nameof(IRawJsonEventUpcaster)}, so it cannot be used by a raw-JSON walk ({nameof(UpcastRaw)}).");

            if (node.Depth >= depthLimit)
                throw UpcasterCycleException.ForBranchDepthExceeded(node.Key.EventType, node.Key.SchemaVersion, depthLimit, BuildPath(node.Key));

            foreach (var produced in rawUpcaster.UpcastRaw(node.Payload, metadata))
            {
                if (PathContains(node.Key, produced.EventType, produced.SchemaVersion))
                {
                    var cyclePath = BuildPath(node.Key);
                    cyclePath.Add((produced.EventType, produced.SchemaVersion));
                    throw UpcasterCycleException.ForCycle(produced.EventType, produced.SchemaVersion, cyclePath);
                }

                var childKey = new PathNode(produced.EventType, produced.SchemaVersion, node.Key);
                queue.Enqueue(new WalkNode<ReadOnlyMemory<byte>>(produced.PayloadJson, childKey, node.Depth + 1));
            }
        }

        return results;
    }

    /// <summary>
    /// The global per-invocation work budget (Q4): the total number of queue iterations plus
    /// terminal events emitted, summed across every branch of a single walk. Scales with
    /// <see cref="Count"/> (the only "shape" signal available, since the port declares no target)
    /// so a chain with more registered steps is granted a proportionally larger budget, while a
    /// combinatorial "diamond lattice" fan-out — whose total work grows like 2^<see cref="Count"/>
    /// — exceeds any such linear budget within a handful of levels.
    /// </summary>
    private int ComputeWorkBudget() => (Count + 1) * WorkBudgetPerUpcaster;

    /// <summary>Checks whether <paramref name="eventType"/>/<paramref name="schemaVersion"/> already appears on the path ending at <paramref name="tail"/> (inclusive).</summary>
    private static bool PathContains(PathNode tail, string eventType, int schemaVersion)
    {
        for (PathNode? current = tail; current is not null; current = current.Parent)
        {
            if (current.SchemaVersion == schemaVersion && string.Equals(current.EventType, eventType, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Materializes the path from the walk's start up to and including <paramref name="tail"/>.</summary>
    private static List<(string EventType, int SchemaVersion)> BuildPath(PathNode tail)
    {
        var reversed = new List<(string EventType, int SchemaVersion)>();
        for (PathNode? current = tail; current is not null; current = current.Parent)
            reversed.Add((current.EventType, current.SchemaVersion));

        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// An immutable, singly-linked node representing one <c>(EventType, SchemaVersion)</c> hop on
    /// a conversion path — siblings created by a 1:N fan-out share the same ancestor chain instead
    /// of each copying it, keeping per-node allocation O(1) regardless of path length.
    /// </summary>
    private sealed record PathNode(string EventType, int SchemaVersion, PathNode? Parent);

    /// <summary>One item of walk queue: the current payload, its path (ending in its own key), and its depth (hop count from the walk's start).</summary>
    private readonly record struct WalkNode<TPayload>(TPayload Payload, PathNode Key, int Depth);
}
