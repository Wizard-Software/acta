namespace Acta.Upcasting;

/// <summary>
/// Thrown when an <see cref="UpcasterChain"/> detects an invalid chain topology — either at
/// construction (an ambiguous or self-referencing registration) or while walking a chain at
/// runtime (a conversion path that revisits a schema it has already visited, a single branch that
/// exceeds its hard iteration limit, or the global per-invocation work budget being exhausted by a
/// combinatorial fan-out).
/// <para>
/// Diagnostic-only shape (SEC-2, ADR-011): this exception carries ONLY schema identifiers — the
/// event type and schema version at the point of failure, plus (when a walk was underway) the
/// ordered path of <c>(EventType, SchemaVersion)</c> pairs visited on the failing branch — and
/// NEVER the raw JSON payload bytes or the deserialized event instance. This mirrors
/// <see cref="Acta.Serialization.UnknownEventTypeException"/>'s diagnostic-only shape.
/// </para>
/// </summary>
public sealed class UpcasterCycleException : Exception
{
    /// <summary>The event type at the center of the failure.</summary>
    public string EventType { get; }

    /// <summary>The schema version at the center of the failure.</summary>
    public int SchemaVersion { get; }

    /// <summary>
    /// The ordered path of <c>(EventType, SchemaVersion)</c> pairs visited on the failing
    /// conversion branch, from the walk's starting point up to and including the point of
    /// failure. Empty for construction-time failures, where no walk has taken place yet.
    /// </summary>
    public IReadOnlyList<(string EventType, int SchemaVersion)> VisitedPath { get; }

    private UpcasterCycleException(
        string message,
        string eventType,
        int schemaVersion,
        IReadOnlyList<(string EventType, int SchemaVersion)> visitedPath)
        : base(message)
    {
        EventType = eventType;
        SchemaVersion = schemaVersion;
        VisitedPath = visitedPath;
    }

    /// <summary>
    /// Creates the exception for a construction-time ambiguous registration: two distinct
    /// upcaster instances both claim the same <c>(EventType, FromSchemaVersion)</c> key, so the
    /// walk would not know which one to apply.
    /// </summary>
    internal static UpcasterCycleException ForDuplicateKey(string eventType, int schemaVersion) => new(
        $"Ambiguous upcaster chain: more than one upcaster is registered for event type " +
        $"'{eventType}' at schema version {schemaVersion}.",
        eventType,
        schemaVersion,
        []);

    /// <summary>
    /// Creates the exception for a construction-time self-referencing registration: the exact
    /// same upcaster instance is registered more than once for the same
    /// <c>(EventType, FromSchemaVersion)</c> key — a self-loop that could never terminate if the
    /// chain allowed it.
    /// </summary>
    internal static UpcasterCycleException ForSelfReference(string eventType, int schemaVersion) => new(
        $"Self-referencing upcaster chain: the same upcaster instance is registered more than " +
        $"once for event type '{eventType}' at schema version {schemaVersion}.",
        eventType,
        schemaVersion,
        []);

    /// <summary>
    /// Creates the exception for an operational cycle detected while walking a chain: the
    /// conversion path revisits a <c>(EventType, SchemaVersion)</c> pair it has already visited.
    /// </summary>
    internal static UpcasterCycleException ForCycle(
        string eventType, int schemaVersion, IReadOnlyList<(string EventType, int SchemaVersion)> visitedPath) => new(
        $"Upcaster chain cycle detected: '{eventType}' at schema version {schemaVersion} is " +
        $"revisited on a single conversion path ({FormatPath(visitedPath)}).",
        eventType,
        schemaVersion,
        visitedPath);

    /// <summary>
    /// Creates the exception for a single branch that exceeded its hard per-branch iteration
    /// limit without terminating — a defense-in-depth backstop against divergence.
    /// </summary>
    internal static UpcasterCycleException ForBranchDepthExceeded(
        string eventType, int schemaVersion, int depthLimit, IReadOnlyList<(string EventType, int SchemaVersion)> visitedPath) => new(
        $"Upcaster chain branch exceeded its hard iteration limit ({depthLimit}) without " +
        $"terminating at '{eventType}' schema version {schemaVersion} ({FormatPath(visitedPath)}).",
        eventType,
        schemaVersion,
        visitedPath);

    /// <summary>
    /// Creates the exception for the global per-invocation work budget being exhausted — the
    /// total number of queue iterations plus terminal events emitted across every branch of a
    /// single <see cref="UpcasterChain.Upcast"/>/<see cref="UpcasterChain.UpcastRaw"/> call
    /// exceeded <paramref name="workBudget"/>. Unlike the per-branch guards, this protects against
    /// a combinatorial fan-out explosion (a converging "diamond" lattice of 1:N upcasters), where
    /// no single path repeats a schema yet the total work across all branches grows combinatorially.
    /// </summary>
    internal static UpcasterCycleException ForWorkBudgetExceeded(
        string eventType, int schemaVersion, int workBudget, IReadOnlyList<(string EventType, int SchemaVersion)> visitedPath) => new(
        $"Upcaster chain exceeded its global per-invocation work budget ({workBudget} steps) " +
        $"while converting toward '{eventType}' schema version {schemaVersion} — likely a " +
        $"combinatorial fan-out ({FormatPath(visitedPath)}).",
        eventType,
        schemaVersion,
        visitedPath);

    private static string FormatPath(IReadOnlyList<(string EventType, int SchemaVersion)> path)
        => path.Count == 0
            ? "(no path recorded)"
            : string.Join(" -> ", path.Select(static p => $"{p.EventType}:{p.SchemaVersion}"));
}
