using Acta.Abstractions;

namespace Acta.Correlation;

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed implementation of <see cref="ICorrelationContextAccessor"/>
/// (MODULE-INTERFACES Grupa 6). The 3-ID rule (<see cref="ICorrelationContext.CorrelationId"/>/
/// <see cref="ICorrelationContext.CausationId"/>/message id) flows through the async call chain
/// via the execution context, with no explicit parameter threading required.
/// <para>
/// <b>R5 (thread-boundary leak).</b> <see cref="AsyncLocal{T}"/> does not survive a hop onto an
/// unrelated pooled thread (e.g. a dispatcher adapter's fire-and-forget enqueue) — the receiving
/// side may observe a foreign ambient context, or none at all. This type only supplies the
/// primitive that closes the leak: <see cref="BeginScope"/> always overwrites whatever ambient
/// value is currently in scope, so a caller that explicitly captures the 3 IDs before the hop and
/// re-seeds them via <see cref="BeginScope"/> on the other side is guaranteed the seed wins over
/// any foreign ambient value — see the accessor test suite (T7/T8) for a falsifiable proof of this
/// precedence. Capturing-and-re-seeding at the actual dispatcher boundary is out of scope for this
/// type (MODULE-INTERFACES Grupa 6 row 6, deferred to the dispatcher-adapter feature).
/// </para>
/// <para>
/// <b>D11 / ADR-011.</b> This accessor never reads <see cref="System.Diagnostics.Activity.Current"/> —
/// <see cref="ICorrelationContext.TraceParent"/>/<see cref="ICorrelationContext.TraceState"/> are
/// carried verbatim only when a caller seeded them; bridging from OpenTelemetry is the
/// responsibility of a host-side decorator, not the core (ADR-011 forbidden boundaries).
/// </para>
/// </summary>
public sealed class AsyncLocalCorrelationContextAccessor : ICorrelationContextAccessor
{
    // Instance field (not static, DP-5): each accessor instance is isolated from every other, so
    // unit tests that create their own `new AsyncLocalCorrelationContextAccessor()` never leak
    // ambient state between test cases. A singleton DI registration still yields exactly one
    // shared instance in production, with per-flow isolation coming from the execution context.
    private readonly AsyncLocal<ICorrelationContext?> _current = new();

    /// <inheritdoc/>
    public ICorrelationContext? Current => _current.Value;

    /// <inheritdoc/>
    public IDisposable BeginScope(ICorrelationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // R5/LIFO: remember the previous value so it can be RESTORED (not cleared) on Dispose,
        // enabling correct nesting of scopes.
        var previous = _current.Value;

        // The explicit seed always wins over whatever ambient value was in scope (R5).
        _current.Value = context;

        return new Scope(this, previous);
    }

    /// <summary>
    /// Disposable handle returned by <see cref="BeginScope"/>. Restores the correlation context
    /// that was in scope immediately before the corresponding <see cref="BeginScope"/> call
    /// (LIFO nesting, DP-4) — never simply clears it to <see langword="null"/>. Idempotent: a
    /// second <see cref="Dispose"/> call is a no-op.
    /// </summary>
    private sealed class Scope(AsyncLocalCorrelationContextAccessor owner, ICorrelationContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner._current.Value = previous;
        }
    }
}
