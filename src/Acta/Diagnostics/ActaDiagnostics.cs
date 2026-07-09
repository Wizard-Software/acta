using System.Diagnostics;

namespace Acta.Diagnostics;

/// <summary>
/// The single, shared <see cref="System.Diagnostics.ActivitySource"/> named <see cref="SourceName"/>
/// (task 8.6, 06-cross-cutting.md §2 — the first <see cref="System.Diagnostics.ActivitySource"/> in the
/// solution, alongside the pre-existing <c>Meter "Acta"</c>) plus the span names and tag keys every
/// instrumented seam in both backends (in-memory and PostgreSQL) uses.
/// <para>
/// <b>Visibility (decision D-2).</b> This type and every constant on it are <see langword="internal"/>
/// — not <see langword="public"/> — mirroring the precedent set by
/// <see cref="Acta.Projections.Daemon.ProjectionDaemonMetrics"/> (Meter/instrument name constants stay
/// <see langword="internal"/> there too). The <i>string values</i> — the span names, the Meter name,
/// the instrument names — are the public observability contract (03-contracts.md §7): a host wires OTLP
/// export by matching those strings, never by referencing this type. Keeping the C# surface
/// <see langword="internal"/> avoids extending the core's public API merely to expose telemetry
/// plumbing. <c>Acta.Postgres</c> — which threads these very same span/tag constants through
/// <c>PostgresEventStore</c> — and both test projects are granted friend access via
/// <c>InternalsVisibleTo</c> on <c>Acta.csproj</c>.
/// </para>
/// <para>
/// <b>Trace context (D11/ADR-011).</b> This task creates the source and its spans only — it does not
/// change how <c>EventMetadata.TraceParent</c>/<c>TraceState</c> are populated or read; that mapping
/// remains the correlation/tracing tie-in tracked separately.
/// </para>
/// </summary>
internal static class ActaDiagnostics
{
    /// <summary>The name of the shared <see cref="ActivitySource"/>, matching the shared <c>Meter "Acta"</c> namespace.</summary>
    internal const string SourceName = "Acta";

    /// <summary>
    /// The single <see cref="ActivitySource"/> every instrumented seam starts its spans from.
    /// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns <see langword="null"/>
    /// when no listener is subscribed — every call site here is null-safe (<c>activity?.</c>), so
    /// observability being inactive costs nothing beyond the null check.
    /// </summary>
    internal static readonly ActivitySource ActivitySource =
        new(SourceName, typeof(ActaDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0");

    /// <summary>The span emitted around one <c>IEventStore.AppendAsync</c> call.</summary>
    internal const string AppendSpan = "acta.append";

    /// <summary>The span emitted around one full stream/all-stream read enumeration (started inside the async-iterator body so it covers the whole enumeration — GAP-6).</summary>
    internal const string ReadSpan = "acta.read";

    /// <summary>The span emitted around one projection's <c>IProjection{TEvent}.ApplyAsync</c> dispatch.</summary>
    internal const string ProjectionApplySpan = "acta.projection.apply";

    /// <summary>The span emitted around one <c>IOutboxFlush.FlushAsync</c> call.</summary>
    internal const string OutboxFlushSpan = "acta.outbox.flush";

    /// <summary>The tag key carrying the stream id on a per-stream append/read span.</summary>
    internal const string StreamIdTag = "acta.stream.id";

    /// <summary>The tag key carrying the number of events an append/read span touched.</summary>
    internal const string EventCountTag = "acta.event.count";

    /// <summary>The tag key identifying which backend produced the span — <c>"inmemory"</c> or <c>"postgres"</c>.</summary>
    internal const string BackendTag = "acta.backend";

    /// <summary>The tag key carrying the projection name on a projection-apply span.</summary>
    internal const string ProjectionNameTag = "projection.name";
}
