using System.Diagnostics.Metrics;

using Xunit;

using Acta.Diagnostics;

namespace Acta.Tests.Diagnostics;

/// <summary>
/// Direct unit tests for <see cref="SubscriptionMetrics"/> (task 8.6, decision D-5): meter ownership
/// (mirrors <see cref="EventStoreMetrics"/>'s D3 contract exactly), the reserved
/// <see cref="SubscriptionMetrics.RecordOverflow"/> instrument's published metadata, and
/// <see cref="SubscriptionMetrics.RecordOverflow"/> itself — which has no production call site in
/// this release, so it must be exercised directly.
/// </summary>
public sealed class SubscriptionMetricsTests
{
    /// <summary>
    /// A minimal <see cref="IMeterFactory"/> test double: creates a real <see cref="Meter"/> from the
    /// requested <see cref="MeterOptions"/>, mirroring a host's <c>AddMetrics()</c>-registered factory
    /// (which owns and disposes every <see cref="Meter"/> it creates).
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
        // D3 (mirrors EventStoreMetrics): the factory owns/disposes the Meter it creates, so
        // SubscriptionMetrics must not dispose it itself. Observed via the RecordOverflow ->
        // measurement round trip: a disposed Meter silently no-ops further measurements, so
        // recording AFTER Dispose() must still be observed if the Meter is genuinely alive.
        long observed = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SubscriptionMetrics.MeterName
                    && instrument.Name == SubscriptionMetrics.LiveBufferOverflowInstrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref observed, measurement));
        listener.Start();

        var metrics = new SubscriptionMetrics(new FakeMeterFactory());
        metrics.Dispose();

        metrics.RecordOverflow();

        Interlocked.Read(ref observed).Should().Be(1);
    }

    [Fact]
    public void Constructor_LiveBufferOverflowCounter_IsPublishedWithOverflowsUnitAndReservedDescription()
    {
        Instrument? published = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SubscriptionMetrics.MeterName
                    && instrument.Name == SubscriptionMetrics.LiveBufferOverflowInstrumentName)
                {
                    published = instrument;
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();

        using var metrics = new SubscriptionMetrics();

        published.Should().NotBeNull();
        published!.Unit.Should().Be("overflows");
        published.Description.Should().Be("Reserved — live subscription buffer overflow (wired in R3; no production caller yet).");
    }

    [Fact]
    public void RecordOverflow_Called_IncrementsLiveBufferOverflowCounterByExactlyOne()
    {
        long count = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SubscriptionMetrics.MeterName
                    && instrument.Name == SubscriptionMetrics.LiveBufferOverflowInstrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref count, measurement));
        listener.Start();

        using var metrics = new SubscriptionMetrics();
        var before = Interlocked.Read(ref count);

        metrics.RecordOverflow();

        (Interlocked.Read(ref count) - before).Should().Be(1);
    }
}
