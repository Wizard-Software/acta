using System.Diagnostics.Metrics;

using Acta.Projections.Daemon;

namespace Acta.Tests.TestSupport;

/// <summary>
/// Observes the <c>acta.projection.gaps_skipped</c> counter (task 5.3) via <see cref="MeterListener"/>,
/// aggregating only measurements tagged with a specific projection name. Every
/// <see cref="ProjectionDaemonMetrics"/> instance without an <see cref="IMeterFactory"/> creates its
/// own fallback <c>Meter("Acta")</c> (decision D3) — since xUnit may run test classes concurrently,
/// filtering by the projection-name tag (unique per test) keeps this observer immune to
/// measurements a different, concurrently-running test's own "Acta" Meter publishes.
/// </summary>
public sealed class GapsSkippedCounterObserver : IDisposable
{
    private readonly MeterListener _listener;
    private readonly string _projectionName;
    private long _count;

    /// <summary>Starts observing immediately — construct before the code under test records anything.</summary>
    /// <param name="projectionName">The projection name tag to filter measurements by.</param>
    /// <exception cref="ArgumentException"><paramref name="projectionName"/> is null or empty.</exception>
    public GapsSkippedCounterObserver(string projectionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);

        _projectionName = projectionName;
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ProjectionDaemonMetrics.MeterName
                    && instrument.Name == ProjectionDaemonMetrics.GapsSkippedInstrumentName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    /// <summary>The total gap-skip count observed for this instance's projection name so far.</summary>
    public long Count => Interlocked.Read(ref _count);

    private void OnMeasurement(
        Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == ProjectionDaemonMetrics.ProjectionNameTag
                && _projectionName.Equals(tag.Value as string, StringComparison.Ordinal))
            {
                Interlocked.Add(ref _count, measurement);
                return;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _listener.Dispose();
}
