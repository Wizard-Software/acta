using System.Collections.Concurrent;

using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local implementation of <see cref="ISnapshotStore"/> (Tier 1) — the default
/// backend registered by <c>AddActa()</c>.
/// <para>
/// <b>Storage:</b> a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by
/// <see cref="Snapshot.StreamId"/>, holding at most one — the latest — <see cref="Snapshot"/> per
/// stream.
/// </para>
/// <para>
/// <b>Schema rejection (ADR-006 Enforcement MUST, task 6.1 decision OQ-2):</b> <see cref="LoadAsync"/>
/// rejects on ANY <see cref="Snapshot.SchemaVersion"/> mismatch against the caller's
/// <c>expectedSchemaVersion</c> — a strictly older AND a strictly newer/unknown stored schema both
/// return <see langword="null"/>, forcing a full rebuild from events. A newer/unknown schema is
/// rejected, not merely an older one, because accepting it would let a state shape the running code
/// does not understand be silently folded into state (SEC-1).
/// </para>
/// <para>
/// <b>CAS by version (D14, multi-pod safety):</b> <see cref="SaveAsync"/> only overwrites the
/// stored entry when the incoming <see cref="Snapshot.Version"/> is STRICTLY GREATER than the
/// existing one's; the whole compare-and-replace runs inside a single atomic
/// <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate{TArg}(TKey, System.Func{TKey,TArg,TValue}, System.Func{TKey,TValue,TArg,TValue}, TArg)"/>
/// delegate — never a <c>TryGetValue</c>-then-write span, which would be a lost-update race. A lost
/// race (an equal or lower incoming version) is a silent no-op, never an exception: two pods racing
/// to snapshot the same stream after reading the same tail is a normal, safe occurrence (ADR-014).
/// </para>
/// <para>
/// <b>Multi-pod behavior class (ADR-014, D14): single-process ONLY.</b> All state lives in this
/// instance's process-local memory; nothing is shared or coordinated across pods. Use a durable
/// backend (Feature 7) for any topology with more than one pod.
/// </para>
/// </summary>
public sealed class InMemorySnapshotStore : ISnapshotStore
{
    private readonly ConcurrentDictionary<string, Snapshot> _snapshots = new();

    /// <inheritdoc/>
    public ValueTask<Snapshot?> LoadAsync(string streamId, int expectedSchemaVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);
        ct.ThrowIfCancellationRequested();

        Snapshot? result = _snapshots.TryGetValue(streamId, out var stored) && stored.SchemaVersion == expectedSchemaVersion
            ? stored
            : null;

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc/>
    public ValueTask SaveAsync(Snapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        // The whole compare-and-replace runs inside the atomic update delegate (see class
        // remarks) — a lost race (equal or lower incoming version) keeps the existing entry
        // untouched, silently, never throwing.
        _snapshots.AddOrUpdate(
            snapshot.StreamId,
            static (_, incoming) => incoming,
            static (_, current, incoming) => incoming.Version > current.Version ? incoming : current,
            snapshot);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DeleteAsync(string streamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamId);
        ct.ThrowIfCancellationRequested();

        _snapshots.TryRemove(streamId, out _);
        return ValueTask.CompletedTask;
    }
}
