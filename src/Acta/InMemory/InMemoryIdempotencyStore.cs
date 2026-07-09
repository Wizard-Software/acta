using System.Collections.Concurrent;

using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local implementation of <see cref="IIdempotencyStore"/> (FR-7, ADR-003) — the
/// default backend used by <c>AddActa()</c>.
/// <para>
/// <b>Concurrency design (best-effort, single-process).</b> Backed by a single
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by <c>(tenantId, idempotencyKey)</c> — plain
/// value-tuple/string equality, i.e. ordinal (no culture-aware comparison); per-tenant isolation is
/// carried by the <c>tenantId</c> component of the key (ADR-016). Every mutating
/// operation runs a lock-free CAS retry loop over the dictionary's atomic
/// <c>TryAdd</c>/<c>TryUpdate</c> overloads instead of taking a lock: a lost race means another
/// thread made progress on the same key, so the loop simply re-reads and retries. Because
/// <see cref="Entry"/> is an immutable record there is no ABA hazard. This is a best-effort
/// guarantee, not hard linearizability — acceptable for a single-process topology (ADR-009/ADR-003);
/// use the Postgres backend (<c>AddActaPostgres</c>) for any topology with more than one pod
/// (ADR-014).
/// </para>
/// <para>
/// <b>Register.</b> <see cref="TryRegisterAsync"/> grants execution (returns <see langword="true"/>)
/// when the key is new, or when an existing entry is <i>expired</i> (lazy re-registration, which
/// resets the remembered result to <see langword="null"/> — the command becomes executable again);
/// it reports a duplicate (<see langword="false"/>) for an active registration. A duplicate is
/// always a boolean result, never an exception (ADR-003).
/// </para>
/// <para>
/// <b>No PII in diagnostics (ADR-008).</b> This type has no <c>ILogger</c> dependency — the simplest
/// way to guarantee <c>idempotencyKey</c>/<c>result</c> are never logged.
/// </para>
/// <para>
/// <b>Unbounded memory growth (known limitation).</b> Unlike the Postgres backend, this store has no
/// housekeeping sweep of expired entries — distinct idempotency keys accumulate for the lifetime of
/// the process; only a re-registration of the <i>same</i> expired key reclaims its slot. This is
/// consistent with the rest of the in-memory, single-process backend family (e.g.
/// <see cref="InMemoryEventStore"/>) and the accepted stance that a production, long-running or
/// high-throughput topology uses the Postgres backend with its daemon-driven sweep instead
/// (ADR-009/ADR-014).
/// </para>
/// </summary>
/// <param name="clock">
/// Clock used to compute registration expiry, injectable for deterministic tests.
/// <see langword="null"/> (the default) resolves to <see cref="TimeProvider.System"/>.
/// </param>
public sealed class InMemoryIdempotencyStore(TimeProvider? clock = null) : IIdempotencyStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string? TenantId, string Key), Entry> _entries = new();

    /// <inheritdoc/>
    public ValueTask<bool> TryRegisterAsync(string idempotencyKey, TimeSpan retention, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ct.ThrowIfCancellationRequested();

        var key = (tenantId, idempotencyKey);

        // Lock-free CAS retry loop (best-effort, single-process — ADR-003): a lost TryAdd/TryUpdate
        // race means another thread made progress on this exact key, so we simply re-read and retry.
        while (true)
        {
            var now = _clock.GetUtcNow();
            var candidate = new Entry(now + retention, Result: null);

            if (_entries.TryAdd(key, candidate))
            {
                return ValueTask.FromResult(true);
            }

            if (!_entries.TryGetValue(key, out var current))
            {
                continue; // extremely unlikely (no Remove path exists) — retry defensively.
            }

            if (current.ExpiresAt > now)
            {
                return ValueTask.FromResult(false); // active registration — duplicate.
            }

            // Expired: lazy re-registration, CAS-guarded, resetting the remembered result to null.
            if (_entries.TryUpdate(key, candidate, current))
            {
                return ValueTask.FromResult(true);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<ReadOnlyMemory<byte>?> GetResultAsync(string idempotencyKey, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ct.ThrowIfCancellationRequested();

        // Copy-on-read: hand the caller a fresh snapshot so it can never mutate the stored bytes
        // (and vice versa), matching PostgresIdempotencyStore's fresh-array-per-read semantics.
        // Note the explicit nullable target type: a bare ternary would infer byte[] and turn the
        // "no result" branch into a non-null (empty) ReadOnlyMemory<byte>? — the null must stay null.
        if (_entries.TryGetValue((tenantId, idempotencyKey), out var entry) && entry.Result is { } bytes)
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(bytes.ToArray());
        }

        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
    }

    /// <inheritdoc/>
    public ValueTask SaveResultAsync(string idempotencyKey, ReadOnlyMemory<byte> result, string? tenantId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ct.ThrowIfCancellationRequested();

        var key = (tenantId, idempotencyKey);

        // Copy-on-write: snapshot the caller's buffer so a later mutation of a reused/pooled backing
        // array can never alter the stored result, matching PostgresIdempotencyStore's result.ToArray().
        var snapshot = new ReadOnlyMemory<byte>(result.ToArray());

        while (_entries.TryGetValue(key, out var current))
        {
            var updated = current with { Result = snapshot };
            if (_entries.TryUpdate(key, updated, current))
            {
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>An immutable idempotency row: its expiry and the optionally-saved result bytes.</summary>
    private sealed record Entry(DateTimeOffset ExpiresAt, ReadOnlyMemory<byte>? Result);
}
