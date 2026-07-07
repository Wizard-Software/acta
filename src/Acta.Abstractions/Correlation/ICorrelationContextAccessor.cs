namespace Acta.Abstractions;

/// <summary>
/// Accessor over an ambient (<see cref="System.Threading.AsyncLocal{T}"/>-backed) correlation
/// context — implemented in the Acta core (<c>Acta.Correlation.AsyncLocalCorrelationContextAccessor</c>).
/// <para>
/// <b>R5 WARNING:</b> at a thread boundary (e.g. a dispatcher adapter's fire-and-forget hop),
/// the ambient value carried by <see cref="Current"/> is NOT reliable — a pooled thread may carry
/// a foreign (stale) correlation context, or none at all. Callers that enqueue work across such a
/// boundary MUST explicitly capture the 3 IDs (and <see cref="ICorrelationContext.User"/> /
/// <see cref="ICorrelationContext.TenantId"/>) before the hop and re-seed them via
/// <see cref="BeginScope"/> on the receiving side before the first read of <see cref="Current"/>.
/// </para>
/// </summary>
public interface ICorrelationContextAccessor
{
    /// <summary>
    /// The correlation context currently in scope for the calling (async) flow, or
    /// <see langword="null"/> when no scope is active.
    /// </summary>
    ICorrelationContext? Current { get; }

    /// <summary>
    /// Begins a new correlation scope, seeding <see cref="Current"/> with <paramref name="context"/>
    /// for the calling (async) flow. The explicit seed takes precedence over whatever ambient
    /// value was previously in scope (R5). Disposing the returned handle restores the previous
    /// value (LIFO nesting) — it does not simply clear it to <see langword="null"/>.
    /// </summary>
    /// <param name="context">The correlation context to seed the new scope with.</param>
    /// <returns>
    /// An <see cref="IDisposable"/> that, when disposed, restores the correlation context that was
    /// in scope immediately before this call.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    IDisposable BeginScope(ICorrelationContext context);
}
