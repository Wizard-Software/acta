namespace Acta.Abstractions;

/// <summary>
/// Thrown when an append operation violates its optimistic-concurrency guard (FR-2) — the
/// stream's actual version did not match the caller's expected version. See ADR-003 for the
/// full guard matrix (<see cref="ExpectedVersion"/>).
/// </summary>
/// <param name="streamId">Identifier of the stream on which the append was attempted.</param>
/// <param name="expectedVersion">The version the caller expected the stream to be at.</param>
/// <param name="actualVersion">The stream's actual current version.</param>
public sealed class ConcurrencyException(
    string streamId, long expectedVersion, long actualVersion)
    : Exception($"Concurrency conflict on '{streamId}': expected {expectedVersion}, actual {actualVersion}")
{
    /// <summary>Identifier of the stream on which the append was attempted.</summary>
    public string StreamId { get; } = streamId;

    /// <summary>The version the caller expected the stream to be at.</summary>
    public long ExpectedVersion { get; } = expectedVersion;

    /// <summary>The stream's actual current version.</summary>
    public long ActualVersion { get; } = actualVersion;
}
