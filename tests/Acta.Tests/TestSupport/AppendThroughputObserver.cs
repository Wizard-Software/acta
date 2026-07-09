using System.Diagnostics.Metrics;

using Acta.Diagnostics;

namespace Acta.Tests.TestSupport;

/// <summary>
/// Observes the <c>acta.append.throughput</c> counter (task 8.6) via <see cref="MeterListener"/>,
/// aggregating only measurements tagged with a specific backend (<c>"inmemory"</c> or
/// <c>"postgres"</c>). Mirrors <see cref="GapsSkippedCounterObserver"/> (task 5.3): every
/// <see cref="EventStoreMetrics"/> instance without an <see cref="IMeterFactory"/> creates its own
/// fallback <c>Meter("Acta")</c> (decision D3) — since xUnit may run test classes concurrently,
/// filtering by the backend tag keeps this observer from picking up a different, concurrently-running
/// test's own fallback "Acta" Meter, as long as that other test does not tag the SAME backend value.
/// </summary>
public sealed class AppendThroughputObserver : IDisposable
{
    private readonly MeterListener _listener;
    private readonly string _backend;
    private long _count;

    /// <summary>Starts observing immediately — construct before the code under test records anything.</summary>
    /// <param name="backend">The backend tag value to aggregate measurements for — <c>"inmemory"</c> or <c>"postgres"</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="backend"/> is null or empty.</exception>
    public AppendThroughputObserver(string backend)
    {
        ArgumentException.ThrowIfNullOrEmpty(backend);

        _backend = backend;
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == EventStoreMetrics.MeterName
                    && instrument.Name == EventStoreMetrics.AppendThroughputInstrumentName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    /// <summary>The total append-throughput increment observed for this instance's backend so far.</summary>
    public long Count => Interlocked.Read(ref _count);

    private void OnMeasurement(
        Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == EventStoreMetrics.BackendTag
                && _backend.Equals(tag.Value as string, StringComparison.Ordinal))
            {
                Interlocked.Add(ref _count, measurement);
                return;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _listener.Dispose();
}
