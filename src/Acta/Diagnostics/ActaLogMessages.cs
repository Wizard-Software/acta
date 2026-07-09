using Microsoft.Extensions.Logging;

namespace Acta.Diagnostics;

/// <summary>
/// Zero-allocation, compile-time-checked structured log messages (task 8.6, decision D-4) for the four
/// instrumented seams shared by both event-store backends and the projection/outbox paths: append
/// committed, read completed, projection applied, and outbox flushed. Shared here — rather than
/// duplicated per type — because <c>PostgresEventStore</c> (in the <c>Acta.Postgres</c> assembly, which
/// is granted friend access via <c>InternalsVisibleTo</c>) logs the exact same append/read shapes as
/// <c>InMemoryEventStore</c>, just tagged with a different backend name.
/// <para>
/// <b>SEC-1.</b> Every parameter here is a scalar (a stream id, an event count, a projection name, or a
/// raw <see cref="Acta.Abstractions.GlobalPosition"/> value) — never
/// <see cref="Acta.Abstractions.EventData"/>, <see cref="Acta.Abstractions.EventMetadata"/>,
/// <see cref="Acta.Abstractions.UserRef"/>, or any event payload.
/// </para>
/// </summary>
internal static partial class ActaLogMessages
{
    /// <summary>Logged once an append batch has committed (after the store's write lock/transaction has been released).</summary>
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Appended {EventCount} event(s) to stream {StreamId} at position {GlobalPosition} ({Backend})")]
    internal static partial void AppendCommitted(this ILogger logger, string streamId, int eventCount, long globalPosition, string backend);

    /// <summary>Logged once a stream or all-stream read enumeration has completed. <paramref name="streamId"/> is <see langword="null"/> for an all-stream read.</summary>
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Read completed: {EventCount} event(s) from {Backend} (stream: {StreamId})")]
    internal static partial void ReadCompleted(this ILogger logger, int eventCount, string backend, string? streamId);

    /// <summary>Logged once a projection has applied one event.</summary>
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Projection {ProjectionName} applied event at position {GlobalPosition}")]
    internal static partial void ProjectionApplied(this ILogger logger, string projectionName, long globalPosition);

    /// <summary>Logged once an outbox flush has enlisted its drained integration events into the current transaction.</summary>
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Outbox flush enlisted {EventCount} event(s)")]
    internal static partial void OutboxFlushed(this ILogger logger, int eventCount);
}
