namespace Acta.Abstractions;

/// <summary>Read direction for stream and all-stream reads (FR-3).</summary>
public enum Direction
{
    /// <summary>Read in ascending order (oldest to newest).</summary>
    Forwards,

    /// <summary>Read in descending order (newest to oldest).</summary>
    Backwards,
}
