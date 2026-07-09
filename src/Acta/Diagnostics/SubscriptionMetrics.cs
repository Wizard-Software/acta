using System.Diagnostics.Metrics;

namespace Acta.Diagnostics;

/// <summary>
/// Owns the shared <see cref="Meter"/> named <see cref="MeterName"/> and the reserved
/// <see cref="Counter{T}"/> instrument <see cref="LiveBufferOverflowInstrumentName"/> (task 8.6,
/// decision D-5): the live-subscription buffer overflow counter a future R3 live-tailing subscription
/// source will increment. No production code path calls <see cref="RecordOverflow"/> in this release —
/// the live-buffer subscription mechanism does not exist yet; the instrument exists ahead of time so a
/// host's dashboards/alerts can already be built against the stable instrument name.
/// <para>
/// <b>Meter ownership (decision D3)</b> and <b>visibility (decision D-2)</b> mirror
/// <see cref="EventStoreMetrics"/> exactly — see that type's remarks.
/// </para>
/// </summary>
public sealed class SubscriptionMetrics : IDisposable
{
    /// <summary>The name of the single, shared Meter hosting every Acta metric (06-cross-cutting.md §2).</summary>
    internal const string MeterName = "Acta";

    /// <summary>
    /// The instrument name for the reserved live-buffer-overflow counter — a public telemetry contract
    /// (03-contracts.md §7), wired to a production caller in R3.
    /// </summary>
    internal const string LiveBufferOverflowInstrumentName = "acta.subscription.live_buffer_overflow";

    private readonly Meter _meter;
    private readonly Counter<long> _liveBufferOverflow;
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
    public SubscriptionMetrics(IMeterFactory? meterFactory = null)
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

        _liveBufferOverflow = _meter.CreateCounter<long>(
            LiveBufferOverflowInstrumentName,
            unit: "overflows",
            description: "Reserved — live subscription buffer overflow (wired in R3; no production caller yet).");
    }

    /// <summary>
    /// Reserved for the R3 live-tailing subscription source (decision D-5): records one live-buffer
    /// overflow. Public so the instrument can be exercised ahead of R3 landing, but no production code
    /// path in this release calls it — reserved, wired in R3.
    /// </summary>
    public void RecordOverflow()
    {
        _liveBufferOverflow.Add(1);
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
