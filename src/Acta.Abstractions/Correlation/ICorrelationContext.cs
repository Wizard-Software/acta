namespace Acta.Abstractions;

/// <summary>
/// The current correlation context (the 3-ID rule: <see cref="CorrelationId"/>/
/// <see cref="CausationId"/>/message id). An explicit seed always takes precedence over
/// ambient background state (R5) — see <see cref="ICorrelationContextAccessor.BeginScope"/>.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>Identifier correlating all messages belonging to the same business transaction.</summary>
    Guid CorrelationId { get; }

    /// <summary>Identifier of the message that directly caused the current one.</summary>
    Guid CausationId { get; }

    /// <summary>
    /// The technical pseudonym of the acting user, if any. This MUST be a technical identifier
    /// (e.g. an account GUID or identity-provider subject id) and MUST NEVER carry raw PII
    /// (e-mail, login, display name) — the pseudonym-to-identity mapping is maintained by the
    /// host, outside Acta (ADR-017).
    /// </summary>
    UserRef? User { get; }

    /// <summary>The tenant this context belongs to, for multi-tenant deployments.</summary>
    string? TenantId { get; }

    /// <summary>W3C Trace Context <c>traceparent</c> header, preserving the originating (source) trace across multi-hop calls (D11).</summary>
    string? TraceParent { get; }

    /// <summary>W3C Trace Context <c>tracestate</c> header.</summary>
    string? TraceState { get; }
}
