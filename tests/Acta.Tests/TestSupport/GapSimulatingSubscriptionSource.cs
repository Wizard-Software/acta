using System.Runtime.CompilerServices;

using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// A fully-fake <see cref="ISubscriptionSource"/> for the gap-guard daemon tests (task 5.3):
/// <see cref="ReadBatchAsync"/> always returns an empty batch — simulating a matching-event read that
/// found nothing up to the safe HWM — while <see cref="ReadFromAsync"/> either yields nothing (a true
/// hole reaching the HWM) or one synthetic raw event above the checkpoint (a non-matching tail,
/// correction V-2), controlled by <see cref="HasRawEventAboveCheckpoint"/>.
/// <para>
/// Deliberately independent of the kit's real <c>InMemoryEventStore</c> (which <c>HwmPoller</c> reads
/// to compute the safe HWM): a test seeds the store with real events so the HWM sits above the
/// checkpoint, while this source's own read path stays fully synthetic — the only way to force a
/// "true hole reaching the HWM" scenario the real in-memory backend can never itself produce (its
/// visibility-lag cutback is zero, so its raw stream and its safe HWM never disagree).
/// </para>
/// </summary>
public sealed class GapSimulatingSubscriptionSource : ISubscriptionSource
{
    /// <summary>
    /// Whether <see cref="ReadFromAsync"/> yields one synthetic raw event above the checkpoint
    /// (non-matching tail, V-2 → <c>NoGap</c>) or nothing at all (a true hole → <c>SkipPermanent</c> /
    /// <c>WaitSafeHarbor</c>). Defaults to <see langword="false"/> (a true hole).
    /// </summary>
    public bool HasRawEventAboveCheckpoint { get; set; }

    /// <inheritdoc/>
    public IAsyncEnumerable<StoredEvent> ReadFromAsync(GlobalPosition from, CancellationToken ct = default)
        => ReadFromCoreAsync(from, ct);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<StoredEvent>> ReadBatchAsync(
        GlobalPosition from, int maxCount, IReadOnlySet<string>? eventTypes = null, CancellationToken ct = default)
        => new(Array.Empty<StoredEvent>());

    private async IAsyncEnumerable<StoredEvent> ReadFromCoreAsync(GlobalPosition from, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask; // Synchronous by design (a test double) — silences CS1998.

        if (HasRawEventAboveCheckpoint)
        {
            yield return new StoredEvent(
                Guid.NewGuid(),
                StreamId: "gap-simulating-stream",
                Version: 0,
                GlobalPosition: new GlobalPosition(from.Value + 1),
                EventType: "NonMatchingTailEvent",
                SchemaVersion: 1,
                Payload: ReadOnlyMemory<byte>.Empty,
                Metadata: new EventMetadata { MessageId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), CausationId = Guid.NewGuid() },
                Timestamp: DateTimeOffset.UtcNow);
        }
    }
}
