using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local <see cref="ISubscriptionSource"/> (Tier 1) — a thin adapter over
/// <see cref="IEventStore.ReadAllAsync"/>, which already enforces the ADR-015 safe high-water-mark
/// guard as part of its read contract. For a single-process store the visibility-lag cutback is
/// zero (positions are assigned and published under the same write lock as the append), so every
/// committed event is immediately safe to read.
/// <para>
/// <b><see cref="ReadBatchAsync"/> semantics:</b> the type filter is applied <i>before</i> the
/// <paramref name="maxCount"/> cut, so <c>maxCount</c> counts matching events, not raw events
/// scanned. Scanning past non-matching events is a deliberate Tier-1 cost (the Postgres backend
/// eliminates it with <c>WHERE event_type = ANY</c>). Filtering on <see cref="StoredEvent.EventType"/>
/// (a plain string, no payload deserialization) is permitted for a backend source — the ADR-015
/// ban on materialized-event type filtering targets the daemon, not the source.
/// </para>
/// </summary>
/// <param name="store">The event store this source reads the all-stream from (a port, not a concrete backend — testability + adapter→port direction).</param>
public sealed class InMemorySubscriptionSource(IEventStore store) : ISubscriptionSource
{
    private readonly IEventStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc/>
    // D12: the ct argument is named — the positional form ReadAllAsync(from, ct) would bind ct to
    // the GlobalPosition? upTo parameter and fail to compile. No type filter on the live path.
    public IAsyncEnumerable<StoredEvent> ReadFromAsync(GlobalPosition from, CancellationToken ct = default)
        => _store.ReadAllAsync(from, ct: ct);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<StoredEvent>> ReadBatchAsync(
        GlobalPosition from,
        int maxCount,
        IReadOnlySet<string>? eventTypes = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        ct.ThrowIfCancellationRequested();

        var batch = new List<StoredEvent>();

        // upTo/maxCount default to null so the store yields the full safe stream lazily; the type
        // filter runs BEFORE the maxCount cut, and Take short-circuits enumeration once the batch
        // is full — the intermediate stream stays lazy (only maxCount matching events materialize).
        await foreach (var storedEvent in _store.ReadAllAsync(from, ct: ct))
        {
            if (eventTypes is not null && !eventTypes.Contains(storedEvent.EventType))
            {
                continue;
            }

            batch.Add(storedEvent);
            if (batch.Count == maxCount)
            {
                break;
            }
        }

        return batch;
    }
}
