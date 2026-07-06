using System.Diagnostics.Metrics;

namespace Acta.Projections.Daemon;

/// <summary>
/// Owns the shared <see cref="Meter"/> named <see cref="MeterName"/> (task 5.3, 06-cross-cutting.md
/// §2 — the single Meter that hosts every metric this library emits; this is the first Meter in the
/// solution) and the <see cref="Counter{T}"/> instrument <see cref="GapsSkippedInstrumentName"/> that
/// <see cref="GapGuard.RecordSkip"/> increments whenever a projection's checkpoint is advanced past a
/// permanent <see cref="Abstractions.GlobalPosition"/> gap.
/// <para>
/// <b>Meter ownership (decision D3).</b> When the host registered an <see cref="IMeterFactory"/>
/// (i.e. called <c>AddMetrics()</c>), the Meter is created through it — the factory, not this type,
/// owns and disposes it, which avoids a duplicate/orphaned <see cref="MeterName"/> Meter alongside
/// whatever the factory itself already tracks. When no factory is registered (the composition root
/// does not require <c>AddMetrics()</c> to be called), this type falls back to a privately-owned
/// <c>new Meter(MeterName)</c> and disposes it itself.
/// </para>
/// <para>
/// <b>Visibility (decision D6, corrected).</b> This class is <see langword="public"/> — not the
/// <see langword="internal"/> originally proposed — because it is a required constructor parameter of
/// <see cref="GapGuard"/>, which is itself public for the same reason (threaded through the public
/// <see cref="ProjectionDaemon"/> constructor): a public member cannot expose a less accessible type
/// in its signature (CS0051). <see cref="RecordGapSkipped"/> stays <see langword="internal"/> — the
/// telemetry names (<see cref="MeterName"/>, <see cref="GapsSkippedInstrumentName"/>) are a public
/// observability contract (03-contracts.md §7) that a host wires OTLP export by string, never by
/// referencing this type or its members.
/// </para>
/// </summary>
public sealed class ProjectionDaemonMetrics : IDisposable
{
    /// <summary>The name of the single, shared Meter hosting every Acta metric (06-cross-cutting.md §2).</summary>
    internal const string MeterName = "Acta";

    /// <summary>The instrument name for the gap-skip counter — a public telemetry contract (03-contracts.md §7).</summary>
    internal const string GapsSkippedInstrumentName = "acta.projection.gaps_skipped";

    /// <summary>The tag key carrying the projection name on every recorded gap-skip measurement.</summary>
    internal const string ProjectionNameTag = "projection.name";

    private readonly Meter _meter;
    private readonly Counter<long> _gapsSkipped;
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
    public ProjectionDaemonMetrics(IMeterFactory? meterFactory = null)
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

        _gapsSkipped = _meter.CreateCounter<long>(
            GapsSkippedInstrumentName,
            unit: "gaps",
            description: "Permanent GlobalPosition gaps a projection's checkpoint was advanced past.");
    }

    /// <summary>
    /// Increments <see cref="GapsSkippedInstrumentName"/> by one, tagged with the projection whose
    /// checkpoint was advanced past a permanent gap.
    /// </summary>
    /// <param name="projectionName">The projection the skip is attributed to.</param>
    /// <exception cref="ArgumentException"><paramref name="projectionName"/> is null or empty.</exception>
    internal void RecordGapSkipped(string projectionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);

        _gapsSkipped.Add(1, new KeyValuePair<string, object?>(ProjectionNameTag, projectionName));
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
