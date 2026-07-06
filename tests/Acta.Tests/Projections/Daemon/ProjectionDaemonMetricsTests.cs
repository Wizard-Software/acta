using System.Diagnostics.Metrics;

using Xunit;

using Acta.Projections.Daemon;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Pins the observability contract of <see cref="ProjectionDaemonMetrics"/> (task 5.3, 03-contracts §7):
/// the exact Meter/instrument names and unit a host wires OTLP export against by string, the
/// projection tag, the input guard, and the D3 meter-ownership rule (a host-factory Meter is never
/// disposed by this type, a privately-owned one is). Each test tags with a unique projection name and
/// filters on it, so the global <see cref="MeterListener"/> cannot cross-talk with a metrics instance
/// another test class emits from in parallel.
/// </summary>
public sealed class ProjectionDaemonMetricsTests
{
    private const string InstrumentName = "acta.projection.gaps_skipped";
    private const string MeterName = "Acta";
    private const string ProjectionTag = "projection.name";

    /// <summary>Starts a listener that counts, per (long) measurement, only those tagged with <paramref name="projection"/>.</summary>
    private static MeterListener CountingListener(string projection, Action<long, string?> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MeterName && instrument.Name == InstrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? tagged = null;
            foreach (var tag in tags)
            {
                if (tag.Key == ProjectionTag)
                {
                    tagged = tag.Value as string;
                }
            }

            if (tagged == projection)
            {
                onMeasurement(value, tagged);
            }
        });
        listener.Start();
        return listener;
    }

    [Fact]
    public void RecordGapSkipped_EmitsNamedCounterWithUnitAndProjectionTag()
    {
        var projection = Guid.NewGuid().ToString("N");
        Instrument? published = null;
        var measurements = new List<long>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MeterName && instrument.Name == InstrumentName)
                {
                    published = instrument;
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == ProjectionTag && (tag.Value as string) == projection)
                {
                    measurements.Add(value);
                }
            }
        });
        listener.Start();

        using (var metrics = new ProjectionDaemonMetrics())
        {
            metrics.RecordGapSkipped(projection);
        }

        published.Should().NotBeNull();
        published!.Unit.Should().Be("gaps");
        measurements.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public void RecordGapSkipped_NullOrEmptyProjection_Throws()
    {
        using var metrics = new ProjectionDaemonMetrics();
        Invoking(() => metrics.RecordGapSkipped(null!)).Should().Throw<ArgumentException>();
        Invoking(() => metrics.RecordGapSkipped("")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithMeterFactory_CreatesTheMeterThroughTheFactory()
    {
        using var factory = new RecordingMeterFactory();

        using var metrics = new ProjectionDaemonMetrics(factory);

        factory.CreatedNames.Should().ContainSingle().Which.Should().Be(MeterName);
    }

    [Fact]
    public void Dispose_OwnedMeter_DisposesItSoRecordingStopsEmitting()
    {
        var projection = Guid.NewGuid().ToString("N");
        var count = 0;
        using var listener = CountingListener(projection, (value, _) => count += (int)value);

        var metrics = new ProjectionDaemonMetrics(); // no factory => owns the Meter (D3)
        metrics.RecordGapSkipped(projection);
        metrics.Dispose();
        metrics.RecordGapSkipped(projection); // Meter disposed => inert, no further measurement

        count.Should().Be(1);
    }

    [Fact]
    public void Dispose_FactoryOwnedMeter_LeavesItAliveSoRecordingKeepsEmitting()
    {
        var projection = Guid.NewGuid().ToString("N");
        var count = 0;
        using var listener = CountingListener(projection, (value, _) => count += (int)value);

        using var factory = new RecordingMeterFactory();
        var metrics = new ProjectionDaemonMetrics(factory); // factory owns the Meter (D3)
        metrics.RecordGapSkipped(projection);
        metrics.Dispose(); // must NOT dispose the factory's Meter
        metrics.RecordGapSkipped(projection);

        count.Should().Be(2);
    }

    /// <summary>An <see cref="IMeterFactory"/> that records the names it creates and disposes its own meters.</summary>
    private sealed class RecordingMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public List<string> CreatedNames { get; } = [];

        public Meter Create(MeterOptions options)
        {
            CreatedNames.Add(options.Name);
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
            {
                meter.Dispose();
            }
        }
    }
}
