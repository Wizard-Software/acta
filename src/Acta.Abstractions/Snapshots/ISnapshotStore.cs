namespace Acta.Abstractions;

/// <summary>
/// Port for loading, saving, and invalidating aggregate state snapshots (FR-4, MODULE-INTERFACES
/// Grupa 4, ADR-006) — an optional read-path optimization that lets a caller skip full event
/// replay. Backend implementations live in the core (<c>InMemorySnapshotStore</c>) and,
/// eventually, in the Postgres adapter (Feature 7); both are expected to honor the exact same
/// rejection/CAS contract described here.
/// <para>
/// A snapshot is NEVER the source of truth (ADR-006): every read path that consults a snapshot
/// store falls back to a full rebuild from events whenever <see cref="LoadAsync"/> returns
/// <see langword="null"/> — a missing entry, a <see cref="Snapshot.SchemaVersion"/> mismatch, or a
/// lost CAS race on <see cref="SaveAsync"/> are all silently absorbed, never surfaced as an error.
/// </para>
/// </summary>
public interface ISnapshotStore
{
    /// <summary>
    /// Loads the latest snapshot for <paramref name="streamId"/>, or <see langword="null"/> when
    /// none exists, OR the stored <see cref="Snapshot.SchemaVersion"/> does not exactly match
    /// <paramref name="expectedSchemaVersion"/> — ANY mismatch, older or newer, is rejected (task
    /// 6.1 decision OQ-2, ADR-006 Enforcement MUST). A <see langword="null"/> result means the
    /// caller MUST fall back to a full rebuild from events; this method never throws to signal a
    /// rejected or missing snapshot.
    /// </summary>
    /// <param name="streamId">Identifier of the stream to load a snapshot for.</param>
    /// <param name="expectedSchemaVersion">
    /// The state-shape version the caller's current aggregate code understands — typically the
    /// aggregate's own <c>SnapshotSchemaVersion</c>.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The latest, schema-compatible snapshot, or <see langword="null"/> when none applies.</returns>
    ValueTask<Snapshot?> LoadAsync(string streamId, int expectedSchemaVersion, CancellationToken ct = default);

    /// <summary>
    /// Saves <paramref name="snapshot"/> as the latest snapshot for its stream, guarded by a
    /// compare-and-swap on <see cref="Snapshot.Version"/>: the write only takes effect when
    /// <paramref name="snapshot"/>'s version is STRICTLY GREATER than the currently stored one for
    /// that stream. A lost race (an equal or lower incoming version) is a silent no-op — never an
    /// exception — so that two pods racing to snapshot the same stream is always safe (ADR-014,
    /// D14).
    /// </summary>
    /// <param name="snapshot">The snapshot to save.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask SaveAsync(Snapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// Removes any stored snapshot for <paramref name="streamId"/>, forcing the next
    /// <see cref="LoadAsync"/> call to fall back to a full rebuild from events. A stream with no
    /// stored snapshot is left unchanged (idempotent).
    /// <para>
    /// This method has two independent invalidation roles: (1) after an upcaster or state-shape
    /// change makes a previously stored snapshot's <see cref="Snapshot.SchemaVersion"/> unreachable
    /// going forward; and (2) the GDPR/RODO erasure hook (ADR-008, binding 06-cross-cutting §3.2) —
    /// after a subject's data is erased from the underlying event stream (Forgettable Payloads),
    /// the host MUST call this method so any snapshot captured before erasure is never served
    /// again; the next load rebuilds state from the now-"forgotten" events instead.
    /// </para>
    /// </summary>
    /// <param name="streamId">Identifier of the stream whose snapshot should be invalidated.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    ValueTask DeleteAsync(string streamId, CancellationToken ct = default);
}
