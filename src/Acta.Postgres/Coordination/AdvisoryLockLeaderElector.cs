using System.Data;
using System.Diagnostics.CodeAnalysis;

using Acta.Abstractions;
using Acta.Postgres.Configuration;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace Acta.Postgres.Coordination;

/// <summary>
/// PostgreSQL-backed <see cref="ILeaderElector"/> (ADR-004 — pure Npgsql, no ORM): elects one leader
/// per projection(×tenant) on a session-level <c>pg_try_advisory_lock</c> so that, across any
/// multi-pod topology (ADR-014, D14), exactly one pod owns a given (projection, tenant) slot. Unlike
/// <see cref="Acta.InMemory.InMemoryLeaderElector"/> — a single process that never fails over — this
/// backend's leadership is bound to a live database <b>session</b>: lose the session, lose the lock,
/// and another pod takes over from the last checkpoint (ADR-005, failover).
/// <para>
/// <b>Dedicated connection for the lease's lifetime (ADR-005 MUST).</b> Each acquired lease
/// <i>pins</i> its own <see cref="NpgsqlConnection"/>, checked out from the data source and never
/// returned or reused for query traffic until the lease is disposed — the session-level lock lives on
/// a connection dedicated to holding it, exactly as ADR-005 requires. (A pooled connection cannot be
/// reset while it is checked out, so the lock can never be silently discarded under the lease.) The
/// lease does not construct its own non-pooled connection because <see cref="NpgsqlDataSource"/>
/// deliberately withholds the password from its public <see cref="NpgsqlDataSource.ConnectionString"/>
/// (and may authenticate via a rotating/IAM credential the string never carries): only the data
/// source can open an authenticated connection, so the lease borrows one and holds it. Pool sizing
/// must therefore budget one connection per concurrently-held leadership lease (06-cross-cutting §4).
/// <see cref="ILeadershipLease.DisposeAsync"/> runs <c>pg_advisory_unlock</c> and returns the
/// connection (Npgsql's reset-on-return also runs <c>pg_advisory_unlock_all</c> as a backstop); if
/// the process or network dies first, PostgreSQL drops the backend and releases the lock
/// automatically — the failover path.
/// </para>
/// <para>
/// <b>Key namespacing (R3, security scan #2).</b> Advisory locks live in a database-<i>global</i>
/// integer space, so the lock key is derived from a schema-namespaced string
/// <c>"{schema}:leader:{projection}:{tenant}"</c> hashed by PostgreSQL's <c>hashtextextended(key, 0)</c>
/// — the same hashing idiom the migration lock uses. Without the <see cref="SchemaName"/> namespace,
/// two Acta instances (or unrelated applications) on the same database would collide keys and steal
/// each other's elections. The hash is computed in SQL, not in C#, so there is exactly one source of
/// truth for the key.
/// </para>
/// <para>
/// <b>Key-collision risk (backlog S2 — <c>hashtextextended</c>, 64-bit).</b> <c>hashtextextended</c>
/// maps an arbitrary-length string onto a 64-bit <c>bigint</c>; two <i>different</i> key strings can
/// therefore hash to the <i>same</i> advisory-lock slot. Two independent facets:
/// <list type="bullet">
///   <item>
///     <b>Cross-application / cross-instance collision</b> — the dominant concern (ADR-005, security
///     scan #2), <b>mitigated</b> by the <see cref="SchemaName"/> namespace in every key: distinct
///     schemas (hence distinct Acta instances) cannot alias unless a full 64-bit hash collision also
///     occurs.
///   </item>
///   <item>
///     <b>Intra-instance collision</b> — two different <c>(projection, tenant)</c> slots hashing to
///     the same 64-bit value. By the birthday bound this needs on the order of 2^32 (~4.3 billion)
///     distinct live slots before the collision probability is material; realistic
///     projection×tenant counts (≪ 10^6) make it negligible. Should a collision ever occur, its
///     effect is <b>bounded and consistency-safe</b>: the two colliding slots would share one lock,
///     so only one of them elects a leader (a liveness/parallelism degradation, never a correctness
///     bug) — and the checkpoint write is <i>independently</i> fenced by an <c>owner_token</c> CAS
///     (<see cref="ICheckpointSink"/>, ADR-005), which rejects any zombie write regardless of the
///     lock outcome.
///   </item>
/// </list>
/// Advisory locks occupy a namespace disjoint from row/table locks, so a key collision cannot
/// interfere with ordinary DML. This analysis is mirrored in <c>06-cross-cutting.md</c> §3.1.
/// </para>
/// </summary>
public sealed class AdvisoryLockLeaderElector : ILeaderElector
{
    private const string TryAcquireSql = "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly ILogger<AdvisoryLockLeaderElector>? _logger;

    /// <summary>
    /// Creates an elector that pins one leadership connection from <paramref name="dataSource"/> per
    /// held lease and namespaces every lock key with
    /// <paramref name="options"/>.<see cref="ActaPostgresOptions.SchemaName"/> (validated fail-fast
    /// here, exactly like <c>MigrationRunner</c>/<c>PostgresCheckpointSink</c>).
    /// </summary>
    /// <param name="dataSource">
    /// The data source leadership connections are opened from. Each held lease keeps one connection
    /// checked out for its lifetime (never shared with query traffic — ADR-005); size the pool to
    /// include one connection per concurrently-held lease (06-cross-cutting §4).
    /// </param>
    /// <param name="options">Backend options; the schema name is validated fail-fast in this constructor.</param>
    /// <param name="logger">Optional diagnostics sink — receives only the projection name and tenant scope key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The configured schema name is outside the allow-list.</exception>
    public AdvisoryLockLeaderElector(
        NpgsqlDataSource dataSource,
        ActaPostgresOptions options,
        ILogger<AdvisoryLockLeaderElector>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _schema = SchemaName.Validate(options.SchemaName);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask<ILeadershipLease?> TryAcquireAsync(
        string projectionName, string? tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectionName);
        ct.ThrowIfCancellationRequested();

        var key = LeaderKey(_schema, projectionName, tenantId);

        // Pinned for the lease lifetime (ADR-005): a session-level advisory lock must live on a
        // connection dedicated to holding it, so this connection is not returned until the lease is
        // disposed or its session dies.
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(TryAcquireSql, connection);
            command.Parameters.AddWithValue("key", key);
            var acquired = (bool)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            if (!acquired)
            {
                // Another pod holds the slot; return the connection and report "not leader".
                await connection.DisposeAsync().ConfigureAwait(false);
                _logger?.LogDebug(
                    "Leadership not acquired projection={Projection} tenant={Tenant} (held elsewhere)",
                    projectionName, tenantId ?? string.Empty);
                return null;
            }

            _logger?.LogInformation(
                "Leadership acquired projection={Projection} tenant={Tenant}", projectionName, tenantId ?? string.Empty);
            return new AdvisoryLockLease(connection, key, projectionName, tenantId, _logger);
        }
        catch
        {
            // Never leak the pinned connection on query failure or cancellation.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Builds the schema-namespaced advisory-lock key string for a slot (hashed by
    /// <c>hashtextextended</c> in SQL). Internal for the coordination test suite (tasks 7.5/7.6).
    /// </summary>
    internal static string LeaderKey(string schema, string projectionName, string? tenantId)
        => $"{schema}:leader:{projectionName}:{tenantId ?? string.Empty}";
}

/// <summary>
/// A held advisory-lock leadership grant over a pinned connection. <see cref="DisposeAsync"/> runs
/// <c>pg_advisory_unlock</c> then returns the connection (clean release); an abrupt session loss
/// releases the lock in the database automatically (failover).
/// </summary>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "pg_advisory_unlock and SELECT 1 are constant statements; the lock key travels as an " +
        "NpgsqlParameter and is hashed by hashtextextended in the database — no interpolation.")]
internal sealed class AdvisoryLockLease : ILeadershipLease
{
    private const string UnlockSql = "SELECT pg_advisory_unlock(hashtextextended(@key, 0))";

    private readonly NpgsqlConnection _connection;
    private readonly string _key;
    private readonly ILogger? _logger;
    private int _disposed;

    internal AdvisoryLockLease(
        NpgsqlConnection connection, string key, string projectionName, string? tenantId, ILogger? logger)
    {
        _connection = connection;
        _key = key;
        _logger = logger;
        ProjectionName = projectionName;
        TenantId = tenantId;
    }

    /// <inheritdoc/>
    public string ProjectionName { get; }

    /// <inheritdoc/>
    public string? TenantId { get; }

    /// <summary>
    /// The PID of the PostgreSQL backend holding the lock — the coordination tests (tasks 7.5/7.6)
    /// use it to <c>pg_terminate_backend</c> the leader and assert failover.
    /// </summary>
    internal int BackendProcessId => _connection.ProcessID;

    /// <inheritdoc/>
    public async ValueTask<bool> IsHeldAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _disposed) != 0 || _connection.State != ConnectionState.Open)
        {
            return false;
        }

        try
        {
            await using var command = new NpgsqlCommand("SELECT 1", _connection);
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (NpgsqlException)
        {
            // The dedicated session is dead → the session-level advisory lock is gone → leadership
            // lost (failover). A dead session is a normal outcome here, not an error to surface.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Npgsql throws this when the connection is already broken/closed — same "not held" verdict.
            return false;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_connection.State == ConnectionState.Open)
            {
                await using var command = new NpgsqlCommand(UnlockSql, _connection);
                command.Parameters.AddWithValue("key", _key);
                await command.ExecuteScalarAsync().ConfigureAwait(false);
                _logger?.LogInformation(
                    "Leadership released projection={Projection} tenant={Tenant}", ProjectionName, TenantId ?? string.Empty);
            }
        }
        catch (NpgsqlException)
        {
            // Session already gone → PostgreSQL has already released the lock; nothing to unlock.
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
