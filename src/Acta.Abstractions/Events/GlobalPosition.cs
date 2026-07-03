namespace Acta.Abstractions;

/// <summary>
/// Monotonic all-stream position (backed by a <see cref="long"/>, mirroring a database bigint).
/// Gaps and out-of-order visibility across concurrent appends are the norm, not an error
/// condition (ADR-001) — consumers must not assume contiguity.
/// </summary>
/// <param name="Value">The raw monotonic position value.</param>
public readonly record struct GlobalPosition(long Value) : IComparable<GlobalPosition>
{
    /// <summary>The position before any event has ever been appended.</summary>
    public static readonly GlobalPosition Start = new(0);

    /// <inheritdoc/>
    public int CompareTo(GlobalPosition other) => Value.CompareTo(other.Value);

    /// <summary>Returns <see langword="true"/> if <paramref name="a"/> precedes <paramref name="b"/>.</summary>
    public static bool operator <(GlobalPosition a, GlobalPosition b) => a.Value < b.Value;

    /// <summary>Returns <see langword="true"/> if <paramref name="a"/> follows <paramref name="b"/>.</summary>
    public static bool operator >(GlobalPosition a, GlobalPosition b) => a.Value > b.Value;

    /// <summary>Returns <see langword="true"/> if <paramref name="a"/> does not follow <paramref name="b"/>.</summary>
    public static bool operator <=(GlobalPosition a, GlobalPosition b) => a.Value <= b.Value;

    /// <summary>Returns <see langword="true"/> if <paramref name="a"/> does not precede <paramref name="b"/>.</summary>
    public static bool operator >=(GlobalPosition a, GlobalPosition b) => a.Value >= b.Value;
}
