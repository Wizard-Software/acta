using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// A spying <see cref="ISubscriptionSource"/> decorator for the async daemon tests (task 5.2): it
/// forwards to an inner source while recording how many times <see cref="ReadBatchAsync"/> was
/// called and the <c>maxCount</c> of each call — so a test can assert the P×T → 1 skip (a caught-up
/// projection reads no batch) and the batching contract (every read uses <c>maxCount == BatchSize</c>).
/// </summary>
/// <param name="inner">The real source to forward to (typically an <c>InMemorySubscriptionSource</c>).</param>
public sealed class CountingSubscriptionSource(ISubscriptionSource inner) : ISubscriptionSource
{
    private readonly ISubscriptionSource _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The number of <see cref="ReadBatchAsync"/> calls observed.</summary>
    public int ReadBatchCallCount { get; private set; }

    /// <summary>The <c>maxCount</c> of every <see cref="ReadBatchAsync"/> call, in call order.</summary>
    public List<int> ObservedMaxCounts { get; } = [];

    /// <inheritdoc/>
    public IAsyncEnumerable<StoredEvent> ReadFromAsync(GlobalPosition from, CancellationToken ct = default)
        => _inner.ReadFromAsync(from, ct);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<StoredEvent>> ReadBatchAsync(
        GlobalPosition from,
        int maxCount,
        IReadOnlySet<string>? eventTypes = null,
        CancellationToken ct = default)
    {
        ReadBatchCallCount++;
        ObservedMaxCounts.Add(maxCount);
        return _inner.ReadBatchAsync(from, maxCount, eventTypes, ct);
    }
}
