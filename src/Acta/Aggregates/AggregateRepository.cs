using System.Buffers.Binary;
using System.Text;

using Acta.Abstractions;
using Acta.Serialization;

namespace Acta.Aggregates;

/// <summary>
/// Core implementation of <see cref="IAggregateRepository{TAggregate}"/>: reads fold a stream's
/// history into a fresh aggregate (snapshot-first seam reserved for task 6.1/ADR-006 — Tier 1
/// always reads from the beginning of the stream); writes apply an explicit
/// optimistic-concurrency guard and stamp each appended event with a deterministic
/// <c>EventId</c> derived from the current command's metadata, so a retried command dedups into
/// an idempotent success instead of throwing (D3, ADR-003).
/// <para>
/// Multi-pod behavior class: safe-by-design — stateless beyond its read-only dependencies
/// (<see cref="IEventStore"/>, <see cref="EventSerializer"/>, the two injected delegates);
/// identical behavior on every pod, no shared state and no coordination.
/// </para>
/// </summary>
/// <typeparam name="TAggregate">The concrete aggregate type this repository loads and saves.</typeparam>
public sealed class AggregateRepository<TAggregate> : IAggregateRepository<TAggregate>
    where TAggregate : AggregateRoot, new()
{
    private const ulong Fnv64OffsetBasis = 0xcbf29ce484222325UL;
    private const ulong Fnv64Prime = 0x100000001b3UL;

    private readonly IEventStore _store;
    private readonly EventSerializer _serializer;
    private readonly Func<EventMetadata> _metadataFactory;
    private readonly Func<EventMetadata, string, int, Guid> _eventIdFactory;

    /// <summary>
    /// Creates a repository bound to a store, a serializer, and the command-session seams needed
    /// to stamp every appended event's metadata and deduplication key.
    /// </summary>
    /// <param name="store">The event store to read from and append to.</param>
    /// <param name="serializer">Serializes aggregate events to/from <see cref="EventData"/>/<see cref="StoredEvent"/>.</param>
    /// <param name="metadataFactory">
    /// Supplies the <see cref="EventMetadata"/> for the current command. Invoked once per
    /// <see cref="SaveAsync"/> call and shared across the whole appended batch — every event in a
    /// single <see cref="SaveAsync"/> call carries the same <c>MessageId</c>/<c>CorrelationId</c>/<c>CausationId</c>.
    /// <para>
    /// The supplied metadata's <see cref="EventMetadata.User"/> MUST carry only a technical
    /// <see cref="UserRef"/> pseudonym, never raw PII (ADR-017) — this repository does not
    /// enforce that itself; enforcement is the host's responsibility.
    /// </para>
    /// </param>
    /// <param name="eventIdFactory">
    /// Optional override for the deterministic per-event <c>EventId</c> derivation; defaults to
    /// an FNV-1a hash of <c>(metadata.MessageId, streamId, index)</c> — see the remarks on the
    /// default derivation for why a non-cryptographic hash is used deliberately.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="store"/>, <paramref name="serializer"/>, or <paramref name="metadataFactory"/> is <see langword="null"/>.
    /// </exception>
    public AggregateRepository(
        IEventStore store,
        EventSerializer serializer,
        Func<EventMetadata> metadataFactory,
        Func<EventMetadata, string, int, Guid>? eventIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(metadataFactory);

        _store = store;
        _serializer = serializer;
        _metadataFactory = metadataFactory;
        _eventIdFactory = eventIdFactory ?? DefaultEventId;
    }

    /// <inheritdoc/>
    public async ValueTask<TAggregate?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var history = await LoadHistoryAsync(id, ct).ConfigureAwait(false);
        if (history.Count == 0)
        {
            return null;
        }

        var aggregate = new TAggregate();
        aggregate.LoadFromHistory(history);
        return aggregate;
    }

    /// <inheritdoc/>
    public async ValueTask<AggregateWriteSession<TAggregate>> FetchForWritingAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        var history = await LoadHistoryAsync(id, ct).ConfigureAwait(false);
        var aggregate = new TAggregate();
        if (history.Count > 0)
        {
            aggregate.LoadFromHistory(history);
        }

        return new AggregateWriteSession<TAggregate>(aggregate, aggregate.Version, this);
    }

    /// <inheritdoc/>
    public async ValueTask<AppendResult> SaveAsync(TAggregate aggregate, long expectedVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        // Fail-fast BEFORE the store call (plan 3.2 §9, GAP-1): the port has no id parameter, so
        // the stream id is the aggregate's own Id. Without this guard, a missing Id would surface
        // as an unreadable ArgumentException from deep inside the store instead of a clear one
        // pointing at the real cause.
        ArgumentException.ThrowIfNullOrEmpty(aggregate.Id);

        var uncommitted = aggregate.UncommittedEvents;
        var metadata = _metadataFactory();
        var batch = new EventData[uncommitted.Count];
        for (var i = 0; i < uncommitted.Count; i++)
        {
            var eventId = _eventIdFactory(metadata, aggregate.Id, i);
            batch[i] = _serializer.ToEventData(uncommitted[i], metadata, eventId);
        }

        // An empty batch is delegated to the store as-is rather than short-circuited here: the
        // store's AppendAsync already treats an empty batch as a safe, idempotent no-op and
        // returns a correct AppendResult (current version/position) — this repository has no
        // way to fabricate that GlobalPosition itself (plan 3.2 §9, GAP-2b).
        var result = await _store.AppendAsync(aggregate.Id, expectedVersion, batch, ct).ConfigureAwait(false);
        aggregate.ClearUncommittedEvents();
        return result;
    }

    /// <summary>
    /// Single-pass materialization loader shared by <see cref="GetByIdAsync"/> and
    /// <see cref="FetchForWritingAsync"/>: reads the whole stream once, deserializing each event
    /// as it arrives into a <see cref="List{T}"/>, then lets the caller branch on
    /// <see cref="List{T}.Count"/> — avoiding a second pass over the underlying
    /// <see cref="IAsyncEnumerable{T}"/> (which would otherwise deserialize every payload twice).
    /// </summary>
    private async ValueTask<List<object>> LoadHistoryAsync(string id, CancellationToken ct)
    {
        // Snapshot-first seam (task 6.1/ADR-006): Tier 1 has no snapshot store, so `fromVersion`
        // is always 0. A future snapshot-aware version resolves `fromVersion` from the loaded
        // snapshot's version + 1 right here, before the read below, without changing this
        // method's signature or its callers.
        const long fromVersion = 0;

        var history = new List<object>();
        await foreach (var stored in _store.ReadStreamAsync(id, fromVersion, toVersion: null, Direction.Forwards, ct).ConfigureAwait(false))
        {
            history.Add(_serializer.ToSourcedEvent(stored).Event);
        }

        return history;
    }

    /// <summary>
    /// Default deterministic <c>EventId</c> derivation: an FNV-1a hash of
    /// <c>"{metadata.MessageId:N}:{streamId}:{index}"</c> — the same algorithm as the test kit's
    /// <c>TestEvents.DeterministicId</c>. The same command retried (same
    /// <see cref="EventMetadata.MessageId"/>) regenerates the exact same <c>EventId</c> for the
    /// same batch position, which the store's unconditional <c>(streamId, EventId)</c>
    /// deduplication then recognizes as an idempotent replay (D3, ADR-003) instead of throwing.
    /// </summary>
    /// <remarks>
    /// FNV-1a is a fast, non-cryptographic hash, used here deliberately: the result is a
    /// deduplication key, not a secret, so predictability carries no security cost — while using a
    /// cryptographic hash (MD5/SHA) would trip the security analyzers CA5351/CA5350, which are
    /// treated as build errors in this repository.
    /// </remarks>
    private static Guid DefaultEventId(EventMetadata metadata, string streamId, int index)
    {
        var seed = $"{metadata.MessageId:N}:{streamId}:{index}";
        ReadOnlySpan<byte> data = Encoding.UTF8.GetBytes(seed);
        Span<byte> buffer = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[..8], Fnv1a64(data, Fnv64OffsetBasis));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], Fnv1a64(data, Fnv64Prime));
        return new Guid(buffer);
    }

    private static ulong Fnv1a64(ReadOnlySpan<byte> data, ulong basis)
    {
        var hash = basis;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= Fnv64Prime;
        }

        return hash;
    }
}
