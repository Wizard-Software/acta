namespace Acta.Abstractions;

/// <summary>
/// A point-in-time capture of an aggregate's state (FR-4, MODULE-INTERFACES Grupa 4, ADR-006),
/// used by <see cref="ISnapshotStore"/> to let the read path skip full event replay.
/// </summary>
/// <param name="StreamId">The aggregate's stream identifier this snapshot was taken for.</param>
/// <param name="Version">
/// The stream version this snapshot is current AS OF (0-based, matching <c>AggregateRoot.Version</c>
/// at capture time). A caller that accepts this snapshot resumes reading the stream's tail starting
/// at <c>Version + 1</c>.
/// </param>
/// <param name="SchemaVersion">
/// Version of the STATE SHAPE (not the stream) — the value returned by the aggregate's
/// <c>SnapshotSchemaVersion</c> at capture time. <see cref="ISnapshotStore.LoadAsync"/> rejects a
/// snapshot on ANY mismatch against the caller's currently expected schema version — <b>older OR
/// newer</b> — never on "older only" (task 6.1 decision OQ-2, ADR-006 Enforcement MUST). Accepting
/// a newer/unknown schema would let a state shape the running code does not understand be folded
/// in silently — a read-corruption risk the strict <c>!=</c> comparison closes.
/// </param>
/// <param name="State">
/// The aggregate's serialized state, produced and owned exclusively by the aggregate itself (via
/// its <c>CaptureState</c> override) — this port and its backends are agnostic to the CLR shape of
/// the state they carry.
/// </param>
/// <param name="TakenAt">The wall-clock time at which this snapshot was captured.</param>
public sealed record Snapshot(
    string StreamId,
    long Version,
    int SchemaVersion,
    ReadOnlyMemory<byte> State,
    DateTimeOffset TakenAt);
