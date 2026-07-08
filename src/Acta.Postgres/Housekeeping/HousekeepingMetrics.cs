using System.Diagnostics.Metrics;

namespace Acta.Postgres.Housekeeping;

/// <summary>
/// Owns the <c>acta.housekeeping.purged</c> counter on the shared <see cref="Meter"/> named
/// <see cref="MeterName"/> (06-cross-cutting.md §2 — the single Meter that hosts every metric this
/// library emits; the meter <i>name</i> is the public observability contract, 03-contracts.md §7, wired
/// by a host via OTLP by string, never by referencing this type). Incremented by
/// <see cref="Housekeeper"/> once per swept auxiliary table with the deleted-row count, tagged by
/// <see cref="TableTag"/>.
/// </summary>
/// <remarks>
/// <b>Meter ownership.</b> When the host registered an <see cref="IMeterFactory"/> (called
/// <c>AddMetrics()</c>), the Meter is created through it — the factory owns and disposes it. Otherwise
/// this type falls back to a privately-owned <c>new Meter(MeterName)</c> and disposes it itself. This
/// mirrors <c>Acta.Projections.Daemon.ProjectionDaemonMetrics</c> in the Tier 1 assembly (both export
/// under the same meter name "Acta").
/// </remarks>
public sealed class HousekeepingMetrics : IDisposable
{
    /// <summary>The name of the single, shared Meter hosting every Acta metric (06-cross-cutting.md §2).</summary>
    internal const string MeterName = "Acta";

    /// <summary>The instrument name for the housekeeping purge counter — a public telemetry contract (03-contracts.md §7).</summary>
    internal const string PurgedInstrumentName = "acta.housekeeping.purged";

    /// <summary>The tag key carrying the auxiliary table name on every recorded purge measurement.</summary>
    internal const string TableTag = "table";

    private readonly Meter _meter;
    private readonly Counter<long> _purged;
    private readonly bool _ownsMeter;

    /// <summary>
    /// Creates the metrics owner: uses <paramref name="meterFactory"/> when the host registered one,
    /// otherwise falls back to a privately-owned <c>new Meter(MeterName)</c>.
    /// </summary>
    /// <param name="meterFactory">
    /// The ambient meter factory (registered by the host's <c>AddMetrics()</c>); <see langword="null"/>
    /// triggers the <c>new Meter(MeterName)</c> fallback — the composition root does not require
    /// <c>AddMetrics()</c> to have been called.
    /// </param>
    public HousekeepingMetrics(IMeterFactory? meterFactory = null)
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

        _purged = _meter.CreateCounter<long>(
            PurgedInstrumentName,
            unit: "rows",
            description: "Rows purged from an auxiliary table by the housekeeping sweep.");
    }

    /// <summary>
    /// Records <paramref name="rows"/> purged from <paramref name="table"/>. A zero count is not
    /// recorded (a counter never adds a no-op measurement).
    /// </summary>
    /// <param name="table">The auxiliary table the rows were deleted from (e.g. <c>outbox</c>).</param>
    /// <param name="rows">The number of rows deleted; non-positive counts are ignored.</param>
    /// <exception cref="ArgumentException"><paramref name="table"/> is null or empty.</exception>
    internal void RecordPurged(string table, long rows)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);

        if (rows > 0)
        {
            _purged.Add(rows, new KeyValuePair<string, object?>(TableTag, table));
        }
    }

    /// <summary>Disposes the Meter, but only when this type created and owns it — never a factory-owned Meter.</summary>
    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }
}
