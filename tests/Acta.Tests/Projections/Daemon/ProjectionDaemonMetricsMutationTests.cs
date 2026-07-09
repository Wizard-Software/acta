using System.Diagnostics.Metrics;

using Xunit;

using Acta.Projections.Daemon;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Additional mutation-kill coverage for <see cref="ProjectionDaemonMetrics"/>: pins the exact
/// <see cref="Instrument.Description"/> of the gap-skip counter and the exact
/// <see cref="Instrument.Unit"/> of the projection-lag gauge (task 8.6, D-1) — both are part of the
/// public, string-keyed observability contract (03-contracts.md §7) a host wires OTLP export
/// against, but neither was previously asserted (only the counter's own unit was).
/// </summary>
public sealed class ProjectionDaemonMetricsMutationTests
{
    [Fact]
    public void Constructor_GapsSkippedCounter_HasTheDocumentedDescription()
    {
        Instrument? published = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ProjectionDaemonMetrics.MeterName
                    && instrument.Name == ProjectionDaemonMetrics.GapsSkippedInstrumentName)
                {
                    published = instrument;
                }
            },
        };
        listener.Start();

        using var metrics = new ProjectionDaemonMetrics();

        published.Should().NotBeNull();
        published!.Description.Should().Be("Permanent GlobalPosition gaps a projection's checkpoint was advanced past.");
    }

    [Fact]
    public void Constructor_ProjectionLagGauge_HasPositionsUnit()
    {
        Instrument? published = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ProjectionDaemonMetrics.MeterName
                    && instrument.Name == ProjectionDaemonMetrics.LagInstrumentName)
                {
                    published = instrument;
                }
            },
        };
        listener.Start();

        using var metrics = new ProjectionDaemonMetrics();

        published.Should().NotBeNull();
        published!.Unit.Should().Be("positions");
    }
}
