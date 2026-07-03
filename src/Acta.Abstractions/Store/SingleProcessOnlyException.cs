namespace Acta.Abstractions;

/// <summary>
/// Thrown when a single-process-only component (e.g. an in-memory store) is used in a topology
/// that has been detected to be multi-pod (D14, ADR-014). Single-process-only components keep
/// their guarantees (dedup, reservations, checkpoints, ...) in process-local memory; running
/// them across more than one pod against the same backing store silently breaks those guarantees
/// rather than failing loudly, which is why a best-effort multi-pod detection reports it through
/// this exception instead. Recovery is a configuration change — replace the single-process-only
/// component with its durable, shared counterpart (e.g. register <c>AddActaPostgres</c> instead
/// of <c>AddActa</c>).
/// <para>
/// Tier 1 scope: this type is a required member of the public exception catalog
/// (03-contracts.md §3) but has no throw site yet in this library version — explicit multi-pod
/// detection is not derivable from a store instance alone (it needs host/deployment
/// information) and is a forward dependency of the host/DI layer (05-implementation.md §2).
/// </para>
/// </summary>
/// <param name="componentName">
/// Name of the single-process-only component that was detected running in a multi-pod
/// topology — a technical identifier such as a type name (e.g. <c>"InMemoryEventStore"</c>),
/// never PII.
/// </param>
public sealed class SingleProcessOnlyException(string componentName)
    : Exception($"Single-process-only component '{componentName}' detected in a multi-pod topology.")
{
    /// <summary>Name of the single-process-only component that triggered this exception.</summary>
    public string ComponentName { get; } = componentName;
}
