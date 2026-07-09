using Microsoft.Extensions.Logging;

using Xunit;

using Acta.Abstractions;
using Acta.Diagnostics;
using Acta.InMemory;
using Acta.Tests.TestSupport;

namespace Acta.Tests.InMemory;

/// <summary>
/// Covers <see cref="InMemoryEventStore"/>'s instrumentation call sites (task 8.6) that
/// <c>Acta.Tests.Diagnostics.EventStoreTelemetryTests</c> does not already exercise: the
/// <c>_logger?.AppendCommitted</c>/<c>_logger?.ReadCompleted</c> log statements themselves (as
/// opposed to only the <see cref="System.Diagnostics.Activity"/> span tags), and the
/// <see cref="ActaDiagnostics.ReadSpan"/>/log event count specifically for
/// <see cref="InMemoryEventStore.ReadAllAsync"/> (the existing telemetry test only covers
/// <see cref="InMemoryEventStore.ReadStreamAsync"/>).
/// </summary>
public sealed class InMemoryEventStoreDiagnosticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    private static EventData CreateEventData(string eventType = "TestEvent") =>
        new(Guid.NewGuid(), eventType, 1, new byte[] { 1, 2, 3 }, CreateMetadata());

    private static EventData[] CreateBatch(int count) => [.. Enumerable.Range(0, count).Select(_ => CreateEventData())];

    private static Logger<InMemoryEventStore> CreateLogger(ListLoggerProvider provider) =>
        new(new SingleProviderLoggerFactory(provider));

    [Fact]
    public async Task AppendAsync_LogsAppendCommittedWithStreamIdCountAndPosition()
    {
        var provider = new ListLoggerProvider();
        var store = new InMemoryEventStore(logger: CreateLogger(provider));
        var streamId = $"stream-{Guid.NewGuid():N}";

        await store.AppendAsync(streamId, ExpectedVersion.NoStream, CreateBatch(3), Ct);

        // A "Statement mutation" survivor removes the log call entirely — no entry at all would
        // be recorded, and the content checks below would also catch a wrong event count.
        provider.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("3 event(s)")
            && e.Message.Contains(streamId)
            && e.Message.Contains("position 3"));
    }

    [Fact]
    public async Task ReadAllAsync_MultipleEvents_ReportsAccurateEventCountViaSpanAndLog()
    {
        var provider = new ListLoggerProvider();
        var store = new InMemoryEventStore(logger: CreateLogger(provider));
        var streamId = $"stream-{Guid.NewGuid():N}";
        await store.AppendAsync(streamId, ExpectedVersion.NoStream, CreateBatch(3), Ct);

        using var spans = new ActivitySpanCollector();

        var read = new List<StoredEvent>();
        await foreach (var storedEvent in store.ReadAllAsync(GlobalPosition.Start, ct: Ct))
        {
            read.Add(storedEvent);
        }

        read.Should().HaveCount(3);

        // Kills both the "count++ -> count--" and the "count++ -> ;" survivors: either mutation
        // makes the recorded count diverge from the 3 events actually yielded above.
        var span = spans.FindSpan(ActaDiagnostics.ReadSpan);
        span.Should().NotBeNull();
        ((int)span!.GetTagItem(ActaDiagnostics.EventCountTag)!).Should().Be(3);

        provider.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("3 event(s)"));
    }

    [Fact]
    public async Task ReadStreamAsync_MultipleEvents_LogsReadCompletedWithStreamIdAndCount()
    {
        var provider = new ListLoggerProvider();
        var store = new InMemoryEventStore(logger: CreateLogger(provider));
        var streamId = $"stream-{Guid.NewGuid():N}";
        await store.AppendAsync(streamId, ExpectedVersion.NoStream, CreateBatch(2), Ct);

        await foreach (var _ in store.ReadStreamAsync(streamId, ct: Ct))
        {
        }

        provider.Entries.Should().Contain(e =>
            e.Level == LogLevel.Debug && e.Message.Contains("2 event(s)") && e.Message.Contains(streamId));
    }
}
