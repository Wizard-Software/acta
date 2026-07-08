using System.Collections.Concurrent;

using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local implementation of <see cref="IReservationStore"/> (FR-16, ADR-009) — the
/// default backend used by <c>AddActa()</c>.
/// <para>
/// <b>Concurrency design (best-effort, single-process).</b> Backed by a single
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by <c>(tenantId, scope, value)</c> — plain
/// value-tuple/string equality, i.e. ordinal (no culture-aware comparison); per-tenant isolation is
/// carried by the <c>tenantId</c> component of the key (ADR-016). Every mutating
/// operation runs a lock-free CAS retry loop over the dictionary's atomic
/// <c>TryAdd</c>/<c>TryUpdate</c>/<c>TryRemove</c> overloads instead of taking a lock: a lost race
/// means another thread made progress on the same key, so the loop simply re-reads and retries.
/// Because <see cref="Entry"/> is an immutable record there is no ABA hazard. This is a best-effort
/// guarantee, not hard linearizability — acceptable for a single-process topology (ADR-009); use the
/// Postgres backend (<c>AddActaPostgres</c>) for any topology with more than one pod (ADR-014).
/// </para>
/// <para>
/// <b>Reserve.</b> <see cref="TryReserveAsync"/> grants the reservation (returns
/// <see langword="true"/>) when the key is free, or when an existing entry is <i>unconfirmed and
/// expired</i> (lazy takeover); it reports a collision (<see langword="false"/>) for an active or
/// confirmed entry. A collision is always a boolean result, never an exception (ADR-009).
/// </para>
/// <para>
/// <b>Confirm / release.</b> Both are owner-guarded: only the current <c>ownerId</c> of an
/// unconfirmed entry may confirm (clears the TTL, making the reservation permanent) or release
/// (removes the entry). A confirmed entry, a missing entry, or an entry owned by someone else leaves
/// the call a silent no-op — never an exception.
/// </para>
/// <para>
/// <b>No PII in diagnostics (ADR-008).</b> This type has no <c>ILogger</c> dependency — the simplest
/// way to guarantee <c>scope</c>/<c>value</c>/<c>ownerId</c> are never logged.
/// </para>
/// <para>
/// <b>Unbounded memory growth (known limitation).</b> Like <see cref="InMemoryIdempotencyStore"/>,
/// this store has no housekeeping sweep: a confirmed reservation is permanent by design, and an
/// orphaned unconfirmed-expired entry lingers until the <i>same</i> key is re-reserved (lazy
/// takeover). Distinct reserved values therefore accumulate for the lifetime of the process. This is
/// consistent with the in-memory, single-process backend family and the accepted stance that a
/// production, long-running or high-throughput topology uses the Postgres backend with its
/// daemon-driven sweep instead (ADR-009/ADR-014).
/// </para>
/// </summary>
/// <param name="clock">
/// Clock used to compute reservation expiry, injectable for deterministic tests.
/// <see langword="null"/> (the default) resolves to <see cref="TimeProvider.System"/>.
/// </param>
public sealed class InMemoryReservationStore(TimeProvider? clock = null) : IReservationStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string? TenantId, string Scope, string Value), Entry> _reservations = new();

    /// <inheritdoc/>
    public ValueTask<bool> TryReserveAsync(string scope, string value, string ownerId, TimeSpan ttl, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        ct.ThrowIfCancellationRequested();

        var key = (tenantId, scope, value);

        // Lock-free CAS retry loop (best-effort, single-process — ADR-009): a lost TryAdd/TryUpdate
        // race means another thread made progress on this exact key, so we simply re-read and retry.
        while (true)
        {
            var now = _clock.GetUtcNow();
            var candidate = new Entry(ownerId, now + ttl, Confirmed: false);

            if (_reservations.TryAdd(key, candidate))
            {
                return ValueTask.FromResult(true);
            }

            if (!_reservations.TryGetValue(key, out var current))
            {
                continue; // a concurrent Release removed the entry between TryAdd and TryGetValue.
            }

            if (current.Confirmed || current.ExpiresAt > now)
            {
                return ValueTask.FromResult(false); // active or confirmed reservation — collision.
            }

            // Unconfirmed and expired: lazy takeover, CAS-guarded against a concurrent mutation.
            if (_reservations.TryUpdate(key, candidate, current))
            {
                return ValueTask.FromResult(true);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask ConfirmAsync(string scope, string value, string ownerId, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        ct.ThrowIfCancellationRequested();

        var key = (tenantId, scope, value);

        while (_reservations.TryGetValue(key, out var current))
        {
            if (current.Confirmed || current.OwnerId != ownerId)
            {
                break; // no-op: already permanent, or owned by someone else (ADR-009).
            }

            var confirmed = current with { ExpiresAt = null, Confirmed = true };
            if (_reservations.TryUpdate(key, confirmed, current))
            {
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask ReleaseAsync(string scope, string value, string ownerId, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        ct.ThrowIfCancellationRequested();

        var key = (tenantId, scope, value);

        while (_reservations.TryGetValue(key, out var current))
        {
            if (current.Confirmed || current.OwnerId != ownerId)
            {
                break; // no-op: confirmed reservations are permanent; someone else's is untouched.
            }

            // Value-comparand removal (not TryRemove(key)) — the only correct CAS-safe overload here.
            if (_reservations.TryRemove(new KeyValuePair<(string?, string, string), Entry>(key, current)))
            {
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>An immutable reservation row: its owner, optional TTL, and confirmed flag.</summary>
    private sealed record Entry(string OwnerId, DateTimeOffset? ExpiresAt, bool Confirmed);
}
