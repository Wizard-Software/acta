namespace Acta.Abstractions;

/// <summary>
/// Causation metadata carried by every appended event. The shape is FROZEN (ADR-011 +
/// ADR-017): it MUST NOT gain new top-level properties — extend only via
/// <see cref="Extensions"/>. <see cref="User"/> is a technical pseudonym
/// (<see cref="UserRef"/>), never raw PII; <see cref="TraceParent"/> and
/// <see cref="TraceState"/> carry W3C Trace Context for cross-service correlation.
/// </summary>
public sealed record EventMetadata
{
    /// <summary>Unique identifier of the message that produced this event (one of Young's three IDs).</summary>
    public required Guid MessageId { get; init; }

    /// <summary>Identifier correlating all messages belonging to the same business transaction.</summary>
    public required Guid CorrelationId { get; init; }

    /// <summary>Identifier of the message that directly caused this event.</summary>
    public required Guid CausationId { get; init; }

    /// <summary>The wall-clock time at which the event was produced.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The technical pseudonym of the acting user, if any — NEVER raw PII (ADR-017).</summary>
    public UserRef? User { get; init; }

    /// <summary>The tenant this event belongs to, for multi-tenant deployments.</summary>
    public string? TenantId { get; init; }

    /// <summary>W3C Trace Context <c>traceparent</c> header, preserving the originating trace across hops.</summary>
    public string? TraceParent { get; init; }

    /// <summary>W3C Trace Context <c>tracestate</c> header.</summary>
    public string? TraceState { get; init; }

    /// <summary>
    /// Free-form extension dictionary for host-specific metadata. MUST NOT carry raw PII
    /// (ADR-017) — enforcement of that rule is the host's responsibility.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Extensions { get; init; }
}
