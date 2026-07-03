namespace Acta.Abstractions;

/// <summary>
/// Sentinel values for the optimistic-concurrency guard used by append operations (FR-2).
/// Values <c>&gt;= 1</c> are not sentinels — they express the exact expected version of the
/// last event in the stream. See ADR-003 for the full guard matrix.
/// </summary>
public static class ExpectedVersion
{
    /// <summary>No concurrency guard is applied; EventId-based deduplication remains unconditional.</summary>
    public const long Any = -2;

    /// <summary>The stream must not exist yet.</summary>
    public const long NoStream = -1;

    /// <summary>The stream must already exist and must be empty.</summary>
    public const long EmptyStream = 0;

    /// <summary>The stream must exist, regardless of its current version.</summary>
    public const long StreamExists = -3;
}
