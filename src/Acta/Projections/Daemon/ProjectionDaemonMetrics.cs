using System.Diagnostics.Metrics;

namespace Acta.Projections.Daemon;

/// <summary>
/// Owns the shared <see cref="Meter"/> named <see cref="MeterName"/> (task 5.3, 06-cross-cutting.md
/// §2 — the single Meter that hosts every metric this library emits; this is the first Meter in the
/// solution) and the <see cref="Counter{T}"/> instrument <see cref="GapsSkippedInstrumentName"/> that
/// <see cref="GapGuard.RecordSkip"/> increments whenever a projection's checkpoint is advanced past a
/// permanent <see cref="Abstractions.GlobalPosition"/> gap.
/// <para>
/// <b>Meter ownership (decision D3).</b> When the host registered an <see cref="IMeterFactory"/>
/// (i.e. called <c>AddMetrics()</c>), the Meter is created through it — the factory, not this type,
/// owns and disposes it, which avoids a duplicate/orphaned <see cref="MeterName"/> Meter alongside
/// whatever the factory itself already tracks. When no factory is registered (the composition root
/// does not require <c>AddMetrics()</c> to be called), this type falls back to a privately-owned
/// <c>new Meter(MeterName)</c> and disposes it itself.
/// </para>
/// <para>
/// <b>Visibility (decision D6, corrected).</b> This class is <see langword="public"/> — not the
/// <see langword="internal"/> originally proposed — because it is a required constructor parameter of
/// <see cref="GapGuard"/>, which is itself public for the same reason (threaded through the public
/// <see cref="ProjectionDaemon"/> constructor): a public member cannot expose a less accessible type
/// in its signature (CS0051). <see cref="RecordGapSkipped"/> stays <see langword="internal"/> — the
/// telemetry names (<see cref="MeterName"/>, <see cref="GapsSkippedInstrumentName"/>) are a public
/// observability contract (03-contracts.md §7) that a host wires OTLP export by string, never by
/// referencing this type or its members.
/// </para>
/// <para>
/// <b>Projection lag (task 8.6, decision D-1 — lock-free snapshot).</b> <see cref="LagInstrumentName"/>
/// is an <see cref="ObservableGauge{T}"/> reporting the safe high-water mark minus each async
/// projection's checkpoint. Its callback (<see cref="ObserveLag"/>) runs on the metrics collector
/// thread and must never do async work, I/O, or read <see cref="ProjectionDaemon"/>'s single-threaded
/// state directly (GAP-1/PERF-1). Instead, <see cref="ProjectionDaemon"/> publishes a lock-free
/// snapshot once per tick via <see cref="AsyncProjectionRegistration.PublishLagSnapshot"/>
/// (<see cref="Volatile.Write{T}(ref T, T)"/>), and the callback here only ever
/// <see cref="Volatile.Read{T}(ref T)"/>s the two published fields and subtracts — one measurement per
/// registered projection, tagged <see cref="ProjectionNameTag"/> only (no ×tenant dimension, decision
/// D-5 — the daemon path is single-tenant): O(P), never O(P×T).
/// </para>
/// </summary>
public sealed class ProjectionDaemonMetrics : IDisposable
{
    /// <summary>The name of the single, shared Meter hosting every Acta metric (06-cross-cutting.md §2).</summary>
    internal const string MeterName = "Acta";

    /// <summary>The instrument name for the gap-skip counter — a public telemetry contract (03-contracts.md §7).</summary>
    internal const string GapsSkippedInstrumentName = "acta.projection.gaps_skipped";

    /// <summary>The instrument name for the projection-lag gauge (task 8.6, D-1) — a public telemetry contract (03-contracts.md §7).</summary>
    internal const string LagInstrumentName = "acta.projection.lag";

    /// <summary>The tag key carrying the projection name on every recorded gap-skip or lag measurement.</summary>
    internal const string ProjectionNameTag = "projection.name";

    private readonly Meter _meter;
    private readonly Counter<long> _gapsSkipped;
    private readonly IReadOnlyList<AsyncProjectionRegistration> _registrations;
    private readonly bool _ownsMeter;

    /// <summary>
    /// Creates the metrics owner: uses <paramref name="meterFactory"/> when the host registered one,
    /// otherwise falls back to a privately-owned <c>new Meter(MeterName)</c> (decision D3).
    /// </summary>
    /// <param name="meterFactory">
    /// The ambient meter factory (registered by the host's <c>AddMetrics()</c>); <see langword="null"/>
    /// triggers the <c>new Meter(MeterName)</c> fallback — the composition root does not require
    /// <c>AddMetrics()</c> to have been called.
    /// </param>
    /// <param name="registrations">
    /// Every async projection registration the <see cref="LagInstrumentName"/> gauge reports on (task
    /// 8.6, D-1); <see langword="null"/> (the default) resolves to an empty set (no projections
    /// registered — e.g. a host that never called <c>AddActaAsyncProjection</c>). Enumerated exactly
    /// once here, at construction — the gauge callback must stay O(P), never re-resolve or
    /// re-enumerate a live DI collection on every collector poll.
    /// </param>
    public ProjectionDaemonMetrics(IMeterFactory? meterFactory = null, IEnumerable<AsyncProjectionRegistration>? registrations = null)
    {
        if (meterFactory is not null)
        {
            _meter = meterFactory.Create(MeterName);
            _ownsMeter = false; // The factory owns and disposes every Meter it creates.
        }
        else
        {
            _meter = new Meter(MeterName);
            _ownsMeter = true;
        }

        _registrations = registrations is null ? [] : [.. registrations];

        _gapsSkipped = _meter.CreateCounter<long>(
            GapsSkippedInstrumentName,
            unit: "gaps",
            description: "Permanent GlobalPosition gaps a projection's checkpoint was advanced past.");

        _meter.CreateObservableGauge(
            LagInstrumentName,
            ObserveLag,
            unit: "positions",
            description: "Safe high-water mark minus checkpoint per async projection (lock-free snapshot, task 8.6, D-1).");
    }

    /// <summary>
    /// Increments <see cref="GapsSkippedInstrumentName"/> by one, tagged with the projection whose
    /// checkpoint was advanced past a permanent gap.
    /// </summary>
    /// <param name="projectionName">The projection the skip is attributed to.</param>
    /// <exception cref="ArgumentException"><paramref name="projectionName"/> is null or empty.</exception>
    internal void RecordGapSkipped(string projectionName)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);

        _gapsSkipped.Add(1, new KeyValuePair<string, object?>(ProjectionNameTag, projectionName));
    }

    /// <summary>
    /// The <see cref="LagInstrumentName"/> <see cref="ObservableGauge{T}"/> callback (task 8.6, decision
    /// D-1). Runs on the metrics collector thread: performs ONLY <see cref="Volatile.Read{T}(ref T)"/>
    /// (via <see cref="AsyncProjectionRegistration.HwmSnapshot"/> and
    /// <see cref="AsyncProjectionRegistration.CheckpointSnapshot"/>) and a subtraction — it never
    /// awaits, performs I/O, calls <c>HwmPoller</c>, or reads
    /// <see cref="AsyncProjectionRegistration.CachedCheckpoint"/> directly; any of those would be
    /// sync-over-async or a data race with the daemon's single background thread (GAP-1/PERF-1).
    /// </summary>
    private IEnumerable<Measurement<long>> ObserveLag()
    {
        foreach (var registration in _registrations)
        {
            yield return new Measurement<long>(
                registration.HwmSnapshot - registration.CheckpointSnapshot,
                new KeyValuePair<string, object?>(ProjectionNameTag, registration.Name));
        }
    }

    /// <summary>Disposes the Meter, but only when this type created and owns it (D3) — never a factory-owned Meter.</summary>
    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }
}
