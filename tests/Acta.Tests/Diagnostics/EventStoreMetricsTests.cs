using System.Diagnostics.Metrics;

using Xunit;

using Acta.Diagnostics;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Diagnostics;

/// <summary>
/// Direct unit tests for <see cref="EventStoreMetrics"/> (task 8.6, decision D3): meter ownership
/// (factory-provided vs. the privately-owned fallback <c>Meter</c>) and the <c>Dispose()</c>
/// contract that must dispose ONLY the Meter this type itself created — never a factory-owned one.
/// </summary>
public sealed class EventStoreMetricsTests
{
    /// <summary>
    /// A minimal <see cref="IMeterFactory"/> test double: creates a real <see cref="Meter"/> from
    /// the requested <see cref="MeterOptions"/>, mirroring a host's <c>AddMetrics()</c>-registered
    /// factory (which owns and disposes every <see cref="Meter"/> it creates).
    /// </summary>
    private sealed class FakeMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
            // The real IMeterFactory owns/disposes its Meters; not exercised by these tests.
        }
    }

    [Fact]
    public void Dispose_WithMeterFactoryProvided_DoesNotDisposeTheFactoryOwnedMeter()
    {
        // D3: when a factory is supplied, the FACTORY owns and disposes the Meter — EventStoreMetrics
        // must not dispose it itself. Observed indirectly: recording AFTER Dispose() must still reach
        // the (still-alive) Counter, since a disposed Meter silently no-ops further measurements.
        var backend = $"factory-owned-{Guid.NewGuid():N}";
        using var observer = new AppendThroughputObserver(backend);

        var metrics = new EventStoreMetrics(new FakeMeterFactory());
        metrics.Dispose();

        metrics.RecordAppend(1, backend);

        observer.Count.Should().Be(1);
    }

    [Fact]
    public void Dispose_WithoutMeterFactory_DisposesItsOwnFallbackMeter()
    {
        // D3: with no factory, EventStoreMetrics creates and OWNS its fallback Meter, so Dispose()
        // must dispose it — recording AFTER Dispose() must silently no-op (disposed Meters do not
        // throw on further Counter.Add calls, they just stop publishing measurements).
        var backend = $"fallback-owned-{Guid.NewGuid():N}";
        using var observer = new AppendThroughputObserver(backend);

        var metrics = new EventStoreMetrics();
        metrics.Dispose();

        metrics.RecordAppend(1, backend);

        observer.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_AppendThroughputCounter_IsPublishedWithEventsUnit()
    {
        Instrument? published = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == EventStoreMetrics.MeterName
                    && instrument.Name == EventStoreMetrics.AppendThroughputInstrumentName)
                {
                    published = instrument;
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();

        using var metrics = new EventStoreMetrics();

        published.Should().NotBeNull();
        published!.Unit.Should().Be("events");
    }

    [Fact]
    public void RecordAppend_PositiveCount_AddsMeasurementTaggedWithBackend()
    {
        var backend = $"direct-{Guid.NewGuid():N}";
        using var observer = new AppendThroughputObserver(backend);
        using var metrics = new EventStoreMetrics();

        metrics.RecordAppend(5, backend);

        observer.Count.Should().Be(5);
    }

    [Fact]
    public void RecordAppend_NullOrEmptyBackend_ThrowsArgumentException()
    {
        using var metrics = new EventStoreMetrics();

        Invoking(() => metrics.RecordAppend(1, null!)).Should().Throw<ArgumentException>();
        Invoking(() => metrics.RecordAppend(1, string.Empty)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecordAppend_NonPositiveCount_DoesNotAddAnyMeasurement()
    {
        var backend = $"noop-{Guid.NewGuid():N}";
        using var observer = new AppendThroughputObserver(backend);
        using var metrics = new EventStoreMetrics();

        metrics.RecordAppend(0, backend);
        metrics.RecordAppend(-3, backend);

        observer.Count.Should().Be(0);
    }
}
