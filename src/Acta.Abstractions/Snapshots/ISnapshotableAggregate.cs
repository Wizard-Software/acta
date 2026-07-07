namespace Acta.Abstractions;

/// <summary>
/// Opt-in marker an <see cref="AggregateRoot"/> subclass implements to declare that it supports
/// snapshotting via <see cref="AggregateRoot.TakeSnapshot"/>/<see cref="AggregateRoot.RestoreFromSnapshot"/>
/// (FR-4, ADR-006, task 6.1 decision OQ-1).
/// <para>
/// The read path (<c>AggregateRepository{TAggregate}</c>) detects snapshot support with a
/// zero-allocation <see langword="is"/> check (<c>aggregate is ISnapshotableAggregate</c>) — it
/// NEVER calls <see cref="AggregateRoot.TakeSnapshot"/> on a fresh instance merely to probe whether
/// snapshotting is supported. That "serialize-and-discard" probe was rejected during verification
/// (GAP-3/PERF-1): it would force a wasted <see cref="System.Text.Json"/> serialization plus an
/// allocation on every single load, purely to answer a question this marker answers in O(1) with
/// no allocation at all.
/// </para>
/// <para>
/// This interface carries no members by design: the actual capture/restore surface
/// (<see cref="AggregateRoot.SnapshotSchemaVersion"/>, <see cref="AggregateRoot.TakeSnapshot"/>,
/// <see cref="AggregateRoot.RestoreFromSnapshot"/>) already lives on <see cref="AggregateRoot"/>
/// itself as public facades — consistent with the already-public <see cref="AggregateRoot.LoadFromHistory"/>
/// precedent, so no <c>InternalsVisibleTo</c> grant is needed for the core repository to call them.
/// Implementing this marker is the ONLY additional step an aggregate takes to opt in.
/// </para>
/// </summary>
public interface ISnapshotableAggregate
{
}
