using System.Buffers.Binary;
using System.Text;

using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// Builders for sample domain events (Order*) used by the contract and property test suites.
/// EventIds are deterministic on demand: a builder called with no explicit id gets a fresh
/// <see cref="Guid.NewGuid"/> (two calls produce two distinct events), while
/// <see cref="DeterministicId"/> derives a stable id from a text seed so retry / dedup scenarios
/// (keyed on <c>(streamId, EventId)</c> — ADR-003) can express "the same command replayed".
/// </summary>
/// <remarks>
/// <see cref="DeterministicId"/> uses FNV-1a, a fast non-cryptographic hash — deliberately NOT
/// MD5/SHA (which would trip the security analyzers CA5351/CA5350, treated as build errors in this
/// repo). It is a test-only seed generator with no security requirement.
/// </remarks>
public static class TestEvents
{
    private const ulong Fnv64OffsetBasis = 0xcbf29ce484222325UL;
    private const ulong Fnv64Prime = 0x100000001b3UL;

    /// <summary>
    /// Derives a stable <see cref="Guid"/> from <paramref name="seed"/>: the same seed always
    /// yields the same id (across runs and machines), different seeds yield different ids. For
    /// deterministic dedup / retry keys in tests — never for cryptographic use.
    /// </summary>
    public static Guid DeterministicId(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        ReadOnlySpan<byte> data = Encoding.UTF8.GetBytes(seed);
        Span<byte> buffer = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[..8], Fnv1a64(data, Fnv64OffsetBasis));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], Fnv1a64(data, Fnv64Prime));
        return new Guid(buffer);
    }

    /// <summary>An <c>OrderPlaced</c> event; pass <paramref name="eventId"/> for a stable dedup key.</summary>
    public static EventData OrderPlaced(Guid? eventId = null) => Create("OrderPlaced", eventId);

    /// <summary>An <c>OrderCancelled</c> event; pass <paramref name="eventId"/> for a stable dedup key.</summary>
    public static EventData OrderCancelled(Guid? eventId = null) => Create("OrderCancelled", eventId);

    /// <summary>A batch of <paramref name="count"/> mutually-distinct, genuinely-new events.</summary>
    public static EventData[] Distinct(int count, string eventType = "OrderPlaced")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return [.. Enumerable.Range(0, count).Select(_ => Create(eventType, eventId: null))];
    }

    // Payload is a well-formed JSON array ("[1,2,3]") rather than the raw bytes { 1, 2, 3 }: the
    // Postgres backend stores payloads in a jsonb NOT NULL column (frozen migration 0001), so the
    // shared contract suite can only run against real PostgreSQL when TestEvents emits valid JSON
    // (FR-10 — payloads are System.Text.Json). No test asserts the payload bytes, so this is a
    // backward-compatible fixture alignment, not a behavioral change.
    private static EventData Create(string eventType, Guid? eventId) =>
        new(eventId ?? Guid.NewGuid(), eventType, SchemaVersion: 1, "[1,2,3]"u8.ToArray(), CreateMetadata());

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

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
