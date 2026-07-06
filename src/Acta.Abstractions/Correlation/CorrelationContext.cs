namespace Acta.Abstractions;

/// <summary>
/// Immutable carrier of a correlation context, used to seed an
/// <see cref="ICorrelationContextAccessor.BeginScope"/> scope. Mirrors <see cref="EventMetadata"/>:
/// the trace fields are optional, and <see cref="User"/> is EXCLUSIVELY a technical pseudonym
/// (ADR-017) — never raw PII.
/// </summary>
public sealed record CorrelationContext : ICorrelationContext
{
    /// <inheritdoc/>
    public required Guid CorrelationId { get; init; }

    /// <inheritdoc/>
    public required Guid CausationId { get; init; }

    /// <summary>
    /// The technical pseudonym of the acting user, if any. This MUST be a technical identifier
    /// (e.g. an account GUID or identity-provider subject id) and MUST NEVER carry raw PII
    /// (e-mail, login, display name) — the pseudonym-to-identity mapping is maintained by the
    /// host, outside Acta (ADR-017).
    /// </summary>
    public UserRef? User { get; init; }

    /// <inheritdoc/>
    public string? TenantId { get; init; }

    /// <inheritdoc/>
    public string? TraceParent { get; init; }

    /// <inheritdoc/>
    public string? TraceState { get; init; }
}
