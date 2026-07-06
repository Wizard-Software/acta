using Acta.Abstractions;

namespace Acta.Projections.Daemon;

/// <summary>
/// One dead-lettered event: a projection's apply failed persistently (retries exhausted) and the
/// projection's <see cref="ProjectionErrorPolicy"/> chose to skip or pause rather than crash the
/// daemon (task 5.4).
/// </summary>
/// <param name="ProjectionName">The projection that failed to apply the event — the checkpoint key.</param>
/// <param name="TenantId">The tenant the event belongs to; <see langword="null"/> for single-tenant (Tier 1/2).</param>
/// <param name="Position">The event's global position — a technical, non-PII identifier (ADR-001).</param>
/// <param name="Attempts">The total number of apply attempts made — the initial attempt plus every retry.</param>
/// <param name="Error">
/// The exception's type full name and message, joined by <c>": "</c> and truncated to
/// <see cref="DeadLetterBuffer.MaxErrorLength"/> UTF-16 characters. See <see cref="DeadLetterBuffer"/>'s
/// remarks for exactly what this field does, and does not, guarantee about payload/PII exposure.
/// </param>
/// <param name="OccurredAt">The wall-clock time the entry was recorded.</param>
public sealed record DeadLetterEntry(
    string ProjectionName,
    string? TenantId,
    GlobalPosition Position,
    int Attempts,
    string Error,
    DateTimeOffset OccurredAt);

/// <summary>
/// An in-memory, bounded ring buffer of <see cref="DeadLetterEntry"/> records (task 5.4) — the
/// diagnostic trail of every poisoned event a projection's error policy skipped or paused on. Shared
/// as a singleton across every projection led by <c>ProjectionDaemon</c>.
/// <para>
/// <b>Capacity (decision D5).</b> Bounded at <see cref="DefaultCapacity"/> entries; once full,
/// recording a new entry drops the oldest one first (a ring, not an unbounded list) — this keeps a
/// permanently poisoned stream from growing this buffer without limit. The resulting memory ceiling
/// is therefore roughly <see cref="DefaultCapacity"/> × (at most <see cref="MaxErrorLength"/> UTF-16
/// characters of <see cref="DeadLetterEntry.Error"/>, plus a small per-entry overhead) — a few
/// megabytes at the default capacity, never more, regardless of how long a stream stays poisoned.
/// </para>
/// <para>
/// <b>No payload by construction — but a residual caveat on the exception message.</b>
/// <see cref="Record"/> accepts only an <see cref="Exception"/>, never a raw event, its deserialized
/// instance, or <see cref="Acta.Abstractions.StoredEvent.Metadata"/> — this type structurally cannot
/// echo the event payload or metadata into a <see cref="DeadLetterEntry"/> (ADR-008/ADR-017 MUST).
/// The residual risk this does NOT eliminate: a host projection's own <c>ApplyAsync</c> may build the
/// exception it throws from event data (for example <c>$"failed for {order.Email}"</c>) — that text
/// becomes part of <see cref="DeadLetterEntry.Error"/> verbatim, because this buffer cannot tell a
/// host-authored message apart from an innocuous one. Hosts must keep PII out of their own exception
/// messages. This buffer is a retention store (readable at any time via <see cref="Snapshot"/>), not
/// a transient log line, so the same caveat that applies to <c>ProjectionDaemon</c>'s baseline error
/// log applies here too — arguably with slightly higher stakes, since entries persist until evicted.
/// </para>
/// <para>
/// <b>Thread-safety.</b> The daemon records entries from its single <c>ExecuteAsync</c> task, but
/// <see cref="Snapshot"/> may be called concurrently — from a diagnostics endpoint, or from a test —
/// so a lightweight <see cref="Lock"/> guards the ring for both operations.
/// </para>
/// </summary>
public sealed class DeadLetterBuffer
{
    /// <summary>
    /// The maximum UTF-16 length of a recorded <see cref="DeadLetterEntry.Error"/> (4 KB, computed
    /// on the final joined string — ADR-008 §Enforcement).
    /// </summary>
    public const int MaxErrorLength = 4096;

    /// <summary>The ring's capacity — bounds this buffer's memory ceiling (decision D5).</summary>
    public const int DefaultCapacity = 1024;

    private readonly Lock _gate = new();
    private readonly Queue<DeadLetterEntry> _entries = new(DefaultCapacity);

    /// <summary>
    /// Records a poisoned event: builds <see cref="DeadLetterEntry.Error"/> from
    /// <paramref name="exception"/>'s type full name and message (never from the event itself — see
    /// this type's remarks), truncates it to <see cref="MaxErrorLength"/> UTF-16 characters if
    /// necessary, and appends the entry to the ring — dropping the oldest entry first if the ring is
    /// already at <see cref="DefaultCapacity"/> (decision D5).
    /// </summary>
    /// <param name="projectionName">The projection that failed to apply the event.</param>
    /// <param name="tenantId">The tenant the event belongs to; <see langword="null"/> for single-tenant.</param>
    /// <param name="position">The event's global position.</param>
    /// <param name="attempts">The total number of apply attempts made (initial attempt plus every retry).</param>
    /// <param name="exception">The exception the final apply attempt threw.</param>
    /// <param name="occurredAt">The wall-clock time to stamp the entry with.</param>
    /// <exception cref="ArgumentException"><paramref name="projectionName"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public void Record(
        string projectionName,
        string? tenantId,
        GlobalPosition position,
        int attempts,
        Exception exception,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ArgumentNullException.ThrowIfNull(exception);

        var error = $"{exception.GetType().FullName}: {exception.Message}";
        if (error.Length > MaxErrorLength)
        {
            error = error[..MaxErrorLength];
        }

        var entry = new DeadLetterEntry(projectionName, tenantId, position, attempts, error, occurredAt);

        lock (_gate)
        {
            if (_entries.Count >= DefaultCapacity)
            {
                _entries.Dequeue(); // Drop-oldest (D5) — the ring never grows past DefaultCapacity.
            }

            _entries.Enqueue(entry);
        }
    }

    /// <summary>Returns a point-in-time copy of every currently buffered entry, oldest first.</summary>
    /// <returns>An immutable snapshot unaffected by later calls to <see cref="Record"/>.</returns>
    public IReadOnlyList<DeadLetterEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }
}
