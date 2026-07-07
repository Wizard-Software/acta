namespace Acta.Abstractions;

/// <summary>
/// Snapshot-taking policy (FR-4, MODULE-INTERFACES Grupa 4, ADR-006).
/// <para>
/// Defined here for group-4 port completeness, but NOT YET consumed anywhere in this release:
/// <c>AggregateRepository{TAggregate}</c> does not automatically write snapshots on
/// <c>SaveAsync</c>. Introducing an automatic co-N threshold is deliberately deferred to task 9.4,
/// AFTER replay cost is actually measured — never preventively (ADR-006 "polityka domyślnie
/// wyłączona — świadoma decyzja anty-przedwczesnej optymalizacji"). Until then, snapshots are only
/// produced by an explicit, caller-driven <see cref="ISnapshotStore.SaveAsync"/> call.
/// </para>
/// </summary>
/// <param name="EveryNEvents">
/// Take a new snapshot after this many events have been appended since the last one, or
/// <see langword="null"/> for no automatic threshold.
/// </param>
/// <param name="Enabled">
/// Whether automatic snapshot-taking is active. Defaults to <see langword="false"/> — matching
/// ADR-006's "disabled by default" decision.
/// </param>
public sealed record SnapshotPolicy(int? EveryNEvents = null, bool Enabled = false);
