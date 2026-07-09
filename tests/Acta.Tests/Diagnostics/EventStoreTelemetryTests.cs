using System.Diagnostics.Metrics;
using System.Text.Json;

using Xunit;

using Acta.Abstractions;
using Acta.Diagnostics;
using Acta.InMemory;
using Acta.Projections.Daemon;
using Acta.Projections.Inline;
using Acta.Serialization;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Diagnostics;

/// <summary>
/// Integration coverage for AK-5 (task 8.6, 06-cross-cutting.md §2) on the in-memory backend: the
/// four <c>"Acta"</c> <see cref="System.Diagnostics.ActivitySource"/> spans
/// (<see cref="ActaDiagnostics.AppendSpan"/>/<see cref="ActaDiagnostics.ReadSpan"/>/
/// <see cref="ActaDiagnostics.ProjectionApplySpan"/>/<see cref="ActaDiagnostics.OutboxFlushSpan"/>),
/// the <c>acta.append.throughput</c> counter, the <c>acta.projection.lag</c> gauge, the pre-existing
/// (task 5.3) <c>acta.projection.gaps_skipped</c> counter (regression), and the reserved (D-5)
/// <c>acta.subscription.live_buffer_overflow</c> instrument's mere existence. Every test starts its
/// listener/observer BEFORE the operation under test — <see cref="System.Diagnostics.ActivitySource.StartActivity(string, System.Diagnostics.ActivityKind)"/>
/// returns <see langword="null"/> (no span at all, not merely uncollected) unless a listener is
/// already subscribed when the call happens.
/// </summary>
public sealed class EventStoreTelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    private static StoredEvent BuildStoredEvent(EventSerializer serializer, object @event, long globalPosition, string streamId = "stream-1")
    {
        var eventData = serializer.ToEventData(@event, CreateMetadata(), Guid.NewGuid());
        return new StoredEvent(
            eventData.EventId,
            streamId,
            0,
            new GlobalPosition(globalPosition),
            eventData.EventType,
            eventData.SchemaVersion,
            eventData.Payload,
            eventData.Metadata,
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Append_emits_acta_append_span_with_stream_and_count_tags()
    {
        var store = new InMemoryEventStore();
        var streamId = $"stream-{Guid.NewGuid():N}";
        var events = TestEvents.Distinct(3);

        using var spans = new ActivitySpanCollector();

        await store.AppendAsync(streamId, ExpectedVersion.NoStream, events, Ct);

        var span = spans.FindSpan(ActaDiagnostics.AppendSpan);
        span.Should().NotBeNull();
        ((string?)span!.GetTagItem(ActaDiagnostics.StreamIdTag)).Should().Be(streamId);
        ((int)span.GetTagItem(ActaDiagnostics.EventCountTag)!).Should().Be(events.Length);
        ((string?)span.GetTagItem(ActaDiagnostics.BackendTag)).Should().Be("inmemory");
    }

    [Fact]
    public async Task Read_emits_acta_read_span()
    {
        var store = new InMemoryEventStore();
        var streamId = $"stream-{Guid.NewGuid():N}";
        await store.AppendAsync(streamId, ExpectedVersion.NoStream, TestEvents.Distinct(2), Ct);

        using var spans = new ActivitySpanCollector();

        var read = new List<StoredEvent>();
        await foreach (var storedEvent in store.ReadStreamAsync(streamId, ct: Ct))
        {
            read.Add(storedEvent);
        }

        var span = spans.FindSpan(ActaDiagnostics.ReadSpan);
        span.Should().NotBeNull();
        read.Should().HaveCount(2);
        ((string?)span!.GetTagItem(ActaDiagnostics.StreamIdTag)).Should().Be(streamId);
        ((string?)span.GetTagItem(ActaDiagnostics.BackendTag)).Should().Be("inmemory");
        ((int)span.GetTagItem(ActaDiagnostics.EventCountTag)!).Should().Be(2);
    }

    [Fact]
    public async Task Append_records_append_throughput_counter()
    {
        using var observer = new AppendThroughputObserver("inmemory");
        using var metrics = new EventStoreMetrics();
        var store = new InMemoryEventStore(metrics: metrics);
        var events = TestEvents.Distinct(4);

        await store.AppendAsync($"stream-{Guid.NewGuid():N}", ExpectedVersion.NoStream, events, Ct);

        observer.Count.Should().Be(events.Length);
    }

    [Fact]
    public async Task Inline_projection_emits_acta_projection_apply_span()
    {
        var registry = new EventTypeRegistry().Register<Incremented>();
        var serializer = new EventSerializer(registry, JsonSerializerOptions.Default);
        var projection = new RecordingProjection<Incremented>();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var first = BuildStoredEvent(serializer, new Incremented(), globalPosition: 1);
        var second = BuildStoredEvent(serializer, new Incremented(), globalPosition: 2);

        using var spans = new ActivitySpanCollector();

        await runner.RunAsync([first, second], Ct);

        // acta.projection.apply is emitted once per (event, matched projection) apply call — two
        // events dispatched to the one matching projection must yield TWO spans, not one per batch.
        var applySpans = spans.FindSpans(ActaDiagnostics.ProjectionApplySpan);
        applySpans.Should().HaveCount(2);
        applySpans.Should().OnlyContain(a => (string?)a.GetTagItem(ActaDiagnostics.ProjectionNameTag) == projection.GetType().Name);
    }

    [Fact]
    public async Task Outbox_flush_emits_acta_outbox_flush_span()
    {
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        var factory = new InMemoryEventAppendTransactionFactory();
        collector.Collect("first", CreateMetadata());
        collector.Collect("second", CreateMetadata());

        using var spans = new ActivitySpanCollector();

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await flush.FlushAsync(tx, Ct);
            await tx.CommitAsync(Ct);
        }

        var span = spans.FindSpan(ActaDiagnostics.OutboxFlushSpan);
        span.Should().NotBeNull();
        ((int)span!.GetTagItem(ActaDiagnostics.EventCountTag)!).Should().Be(2);
    }

    [Fact]
    public void Projection_lag_gauge_reports_hwm_minus_checkpoint()
    {
        var projectionName = Guid.NewGuid().ToString("N");
        var registry = new EventTypeRegistry().Register<Incremented>();
        var serializer = new EventSerializer(registry, JsonSerializerOptions.Default);
        var runner = new InlineProjectionRunner(serializer, registry, [new RecordingProjection<Incremented>()]);
        var registration = new AsyncProjectionRegistration(projectionName, AsyncProjectionTestKit.IncrementedOnly, runner);

        var measured = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ProjectionDaemonMetrics.MeterName
                    && instrument.Name == ProjectionDaemonMetrics.LagInstrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == ProjectionDaemonMetrics.ProjectionNameTag && (tag.Value as string) == projectionName)
                {
                    measured.Add(value);
                }
            }
        });
        listener.Start();

        using var metrics = new ProjectionDaemonMetrics(registrations: [registration]);

        // Lagging: hwm 10, checkpoint 4 -> lag 6.
        registration.PublishLagSnapshot(hwm: 10, checkpoint: 4);
        listener.RecordObservableInstruments();

        measured.Should().ContainSingle().Which.Should().Be(6);

        // Caught up: hwm == checkpoint -> lag 0.
        measured.Clear();
        registration.PublishLagSnapshot(hwm: 10, checkpoint: 10);
        listener.RecordObservableInstruments();

        measured.Should().ContainSingle().Which.Should().Be(0);
    }

    [Fact]
    public void Gaps_skipped_still_emitted()
    {
        // Regression (task 5.3): 8.6 must not have disturbed the pre-existing gaps-skipped counter.
        var projectionName = Guid.NewGuid().ToString("N");
        using var observer = new GapsSkippedCounterObserver(projectionName);
        using var metrics = new ProjectionDaemonMetrics();

        metrics.RecordGapSkipped(projectionName);

        observer.Count.Should().Be(1);
    }

    [Fact]
    public void Live_buffer_overflow_instrument_exists()
    {
        // D-5: existence-only — the live-buffer subscription source that would call RecordOverflow()
        // does not exist yet (R3). This test proves only that the instrument itself is published.
        var published = false;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SubscriptionMetrics.MeterName
                    && instrument.Name == SubscriptionMetrics.LiveBufferOverflowInstrumentName)
                {
                    published = true;
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();

        using var metrics = new SubscriptionMetrics();

        published.Should().BeTrue();
    }
}
