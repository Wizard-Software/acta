using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.Coordination;
using Acta.Postgres.Tests.Infrastructure;

using Npgsql;

namespace Acta.Postgres.Tests.Coordination;

/// <summary>
/// Test-only model of one "pod" (a node) in a multi-pod deployment, per the harness sketched in
/// TESTING-SPEC §5.2 (<c>CreatePodAsync</c> / <c>AcquireLeadershipAsync</c> / <c>KillConnectionAsync</c> /
/// <c>SaveCheckpointAsync</c>). Each pod owns its own <see cref="AdvisoryLockLeaderElector"/> and
/// <see cref="PostgresCheckpointSink"/> plus a distinct <c>owner_token</c>, all over the shared
/// <see cref="PostgresFixture.DataSource"/> and a single migrated schema — two pods therefore contend
/// on real, separate backend sessions (ADR-014, D14 — multi-pod is baseline, not an extension).
/// <para>
/// The elector pins a leadership connection <i>from the data source</i> (not a bespoke
/// <c>Pooling=false</c> connection), so an abrupt node death is modelled by
/// <see cref="KillLeadershipConnectionAsync"/> — <c>pg_terminate_backend</c> on the lease's
/// <see cref="AdvisoryLockLease.BackendProcessId"/> from a separate admin session (session loss = lock
/// loss = failover). This is the exact primitive task 7.5 used in
/// <c>SessionLoss_ReleasesLock_EnablingFailover</c>.
/// </para>
/// </summary>
internal sealed class Pod(PostgresFixture fixture, ActaPostgresOptions options, string ownerToken)
    : IAsyncDisposable
{
    private readonly PostgresFixture _fixture = fixture;
    private readonly AdvisoryLockLeaderElector _elector = new(fixture.DataSource, options);
    private readonly PostgresCheckpointSink _sink = new(fixture.DataSource, options);
    private ILeadershipLease? _lease;

    /// <summary>This pod's checkpoint <c>owner_token</c> — the fencing identity distinguishing it from peers.</summary>
    public string OwnerToken { get; } = ownerToken;

    /// <summary>True once this pod holds a (locally tracked) leadership lease.</summary>
    public bool IsLeader => _lease is not null;

    /// <summary>
    /// Non-blocking single leadership attempt (mirrors <c>ILeaderElector.TryAcquireAsync</c>): returns
    /// <see langword="true"/> and records the lease when this pod won the slot, else <see langword="false"/>.
    /// </summary>
    public async ValueTask<bool> TryAcquireLeadershipAsync(string projection, string? tenantId, CancellationToken ct)
    {
        _lease = await _elector.TryAcquireAsync(projection, tenantId, ct);
        return _lease is not null;
    }

    /// <summary>
    /// Failover-safe acquire: polls the non-blocking try-lock briefly, because PostgreSQL releases a
    /// terminated backend's advisory locks <i>asynchronously</i> — so the takeover is not observable on
    /// the very first attempt. Deterministic bound (50 × 100 ms), same shape as task 7.5's retry helper.
    /// </summary>
    public async ValueTask<bool> AcquireLeadershipWithFailoverAsync(string projection, string? tenantId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await TryAcquireLeadershipAsync(projection, tenantId, ct))
            {
                return true;
            }

            await Task.Delay(100, ct);
        }

        return false;
    }

    /// <summary>Live liveness check of the held lease's backend session (false once the session is gone).</summary>
    public async ValueTask<bool> IsLeadershipHeldAsync(CancellationToken ct)
        => _lease is not null && await _lease.IsHeldAsync(ct);

    /// <summary>
    /// The crash primitive: abruptly terminate this pod's leadership backend from a separate admin
    /// session (NOT a clean <see cref="IAsyncDisposable.DisposeAsync"/> release). The lost session drops
    /// the advisory lock in PostgreSQL, opening the slot for a peer's failover.
    /// </summary>
    public async ValueTask KillLeadershipConnectionAsync(CancellationToken ct)
    {
        if (_lease is null)
        {
            throw new InvalidOperationException("Pod holds no leadership lease to kill.");
        }

        var backendPid = ((AdvisoryLockLease)_lease).BackendProcessId;
        await using var admin = await _fixture.DataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin);
        command.Parameters.AddWithValue("pid", backendPid);
        await command.ExecuteScalarAsync(ct);
    }

    /// <summary>Persist a fenced checkpoint under this pod's <see cref="OwnerToken"/> (the write a real daemon tick makes).</summary>
    public ValueTask SaveCheckpointAsync(string projection, string? tenantId, long position, CancellationToken ct)
        => _sink.SaveAsync(projection, tenantId, new GlobalPosition(position), OwnerToken, ct);

    /// <summary>Read the durable checkpoint a failing-over pod would resume from.</summary>
    public ValueTask<GlobalPosition?> LoadCheckpointAsync(string projection, string? tenantId, CancellationToken ct)
        => _sink.LoadAsync(projection, tenantId, ct);

    public async ValueTask DisposeAsync()
    {
        if (_lease is not null)
        {
            // Disposing a lease whose session is already dead (post-kill) is safe — see AdvisoryLockLease.
            await _lease.DisposeAsync();
        }
    }
}
