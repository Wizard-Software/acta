using System.Diagnostics.Metrics;

namespace Acta.Diagnostics;

/// <summary>
/// Owns the shared <see cref="Meter"/> named <see cref="MeterName"/> (task 8.6, 06-cross-cutting.md §2
/// — the same Meter <see cref="Acta.Projections.Daemon.ProjectionDaemonMetrics"/> already publishes
/// under; OTel aggregates every owner's instruments by Meter name) and the <see cref="Counter{T}"/>
/// instrument <see cref="AppendThroughputInstrumentName"/> that both event-store backends
/// (<c>InMemoryEventStore</c> and <c>PostgresEventStore</c>) increment on every <c>AppendAsync</c> call.
/// <para>
/// <b>Meter ownership (decision D3).</b> Mirrors <c>ProjectionDaemonMetrics</c> exactly: when the host
/// registered an <see cref="IMeterFactory"/> (called <c>AddMetrics()</c>), the Meter is created through
/// it and the factory owns/disposes it; otherwise this type falls back to a privately-owned
/// <c>new Meter(MeterName)</c> and disposes it itself.
/// </para>
/// <para>
/// <b>Visibility (decision D-2).</b> This class is <see langword="public"/> — required so it can be a
/// constructor parameter of the (also public) <c>InMemoryEventStore</c>/<c>PostgresEventStore</c>
/// (CS0051 — a public member cannot expose a less-accessible type in its signature), mirroring the same
/// reasoning already applied to <c>ProjectionDaemonMetrics</c>. <see cref="RecordAppend"/> and every
/// constant stay <see langword="internal"/> — the instrument/Meter <i>names</i> are the public
/// observability contract (03-contracts.md §7), never this type or its members.
/// </para>
/// </summary>
public sealed class EventStoreMetrics : IDisposable
{
    /// <summary>The name of the single, shared Meter hosting every Acta metric (06-cross-cutting.md §2).</summary>
    internal const string MeterName = "Acta";

    /// <summary>The instrument name for the append-throughput counter — a public telemetry contract (03-contracts.md §7).</summary>
    internal const string AppendThroughputInstrumentName = "acta.append.throughput";

    /// <summary>The tag key carrying the backend ("inmemory" | "postgres") on every recorded append-throughput measurement.</summary>
    internal const string BackendTag = "acta.backend";

    private readonly Meter _meter;
    private readonly Counter<long> _appendThroughput;
    private readonly bool _ownsMeter;

    /// <summary>
    /// Creates the metrics owner: uses <paramref name="meterFactory"/> when the host registered one,
    /// otherwise falls back to a privately-owned <c>new Meter(MeterName)</c> (decision D3).
    /// </summary>
    /// <param name="meterFactory">
    /// The ambient meter factory (registered by the host's <c>AddMetrics()</c>); <see langword="null"/>
    /// triggers the <c>new Meter(MeterName)</c> fallback — the composition root does not require
    /// <c>AddMetrics()</c> to have been called.
    /// </param>
    public EventStoreMetrics(IMeterFactory? meterFactory = null)
    {
        if (meterFactory is not null)
        {
            _meter = meterFactory.Create(MeterName);
            _ownsMeter = false; // The factory owns and disposes every Meter it creates.
        }
        else
        {
            _meter = new Meter(MeterName);
            _ownsMeter = true;
        }

        _appendThroughput = _meter.CreateCounter<long>(
            AppendThroughputInstrumentName,
            unit: "events",
            description: "Events appended to the event store, tagged by backend.");
    }

    /// <summary>
    /// Adds <paramref name="count"/> to <see cref="AppendThroughputInstrumentName"/>, tagged by
    /// <paramref name="backend"/>. A non-positive <paramref name="count"/> is not recorded (mirrors
    /// <c>HousekeepingMetrics.RecordPurged</c> — a counter never adds a no-op measurement).
    /// </summary>
    /// <param name="count">The number of events in the append batch.</param>
    /// <param name="backend">The backend that performed the append — <c>"inmemory"</c> or <c>"postgres"</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="backend"/> is null or empty.</exception>
    internal void RecordAppend(long count, string backend)
    {
        ArgumentException.ThrowIfNullOrEmpty(backend);

        if (count > 0)
        {
            _appendThroughput.Add(count, new KeyValuePair<string, object?>(BackendTag, backend));
        }
    }

    /// <summary>Disposes the Meter, but only when this type created and owns it (D3) — never a factory-owned Meter.</summary>
    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }
}
