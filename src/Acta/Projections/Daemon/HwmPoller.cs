using Acta.Abstractions;
using Acta.Configuration;

namespace Acta.Projections.Daemon;

/// <summary>
/// Reads the safe high-water mark (HWM) of the all-stream once per pod per tick (task 5.2,
/// 05-implementation §3 step 0). The daemon calls this exactly once per <c>RunTickAsync</c> and
/// shares the result across every led projection, collapsing P projections × T ticks of polling to
/// one read per tick (the P×T → 1 optimization).
/// <para>
/// <b>Tier-1 (in-memory).</b> The safe HWM is the head of the all-stream, obtained by reading a
/// single event backwards (<see cref="IEventStore.ReadAllAsync"/> with <c>Direction.Backwards</c>
/// and <c>maxCount: 1</c>). The <see cref="ProjectionDaemonOptions.VisibilityLag"/> cutback is
/// <b>zero</b> for the in-memory backend — positions are assigned and published under the same write
/// lock as the append, so every committed event is immediately safe (the head IS the safe HWM). The
/// time-based cutback (withholding events younger than <c>now - VisibilityLag</c>) is Postgres
/// semantics and arrives with <c>PostgresHwmPoller</c> in Feature 7; the injected
/// <see cref="ProjectionDaemonOptions"/> is carried here for backend parity, not consumed in Tier-1.
/// </para>
/// <para>
/// <b>Cost.</b> The in-memory head read is O(n) time and allocation per tick (the backend buffers
/// the reversed all-stream before taking one) — a deliberate Tier-1 cost with no O(1) head accessor
/// on the port; the Postgres backend replaces it with a cheap <c>SELECT max(global_position)</c>.
/// </para>
/// </summary>
/// <param name="store">The event store the all-stream head is read from (a port — adapter→port direction, testability).</param>
/// <param name="options">The daemon options carrying <see cref="ProjectionDaemonOptions.VisibilityLag"/> (dormant in Tier-1).</param>
public sealed class HwmPoller(IEventStore store, ProjectionDaemonOptions options)
{
    private readonly IEventStore _store = store ?? throw new ArgumentNullException(nameof(store));

    // Carried for backend parity (Postgres VisibilityLag cutback, Feature 7). Dormant in Tier-1:
    // the in-memory head IS the safe HWM (zero cutback). Stored so the constructor parameter is read.
    private readonly ProjectionDaemonOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Returns the safe high-water mark of the all-stream — the highest position safe to consume.
    /// For an empty store, returns <see cref="GlobalPosition.Start"/> (nothing to do).
    /// </summary>
    /// <param name="ct">A token to cancel the read.</param>
    /// <returns>The safe HWM, or <see cref="GlobalPosition.Start"/> when the all-stream is empty.</returns>
    public async ValueTask<GlobalPosition> ReadSafeHighWaterMarkAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Named ct: is mandatory — the positional form ReadAllAsync(from, ct) binds ct to the
        // GlobalPosition? upTo parameter and fails to compile (GAP-1 / D3). Backwards + maxCount:1
        // yields the single highest-positioned event; an empty stream yields nothing.
        await foreach (var stored in _store.ReadAllAsync(
            GlobalPosition.Start, maxCount: 1, direction: Direction.Backwards, ct: ct))
        {
            return stored.GlobalPosition;
        }

        return GlobalPosition.Start;
    }
}
