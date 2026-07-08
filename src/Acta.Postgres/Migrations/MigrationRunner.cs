using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

using Acta.Postgres.Configuration;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace Acta.Postgres.Migrations;

/// <summary>
/// Applies versioned schema migrations shipped as embedded SQL resources against a PostgreSQL
/// database.
/// <para>
/// <b>Discovery (D3):</b> migrations are the assembly's manifest resources whose logical name
/// contains <c>.Migrations.Sql.</c> and whose file name starts with an <c>NNNN_</c> ordinal; they
/// are applied in ascending version order. Adding a new <c>0002_*.sql</c> embedded resource needs
/// no code change.
/// </para>
/// <para>
/// <b>Multi-pod safety + atomicity (D2, ADR-014):</b> the whole run executes in ONE transaction —
/// PostgreSQL supports transactional DDL, so a mid-run failure rolls the schema back entirely
/// (no partial schema). A transaction-scoped advisory lock keyed on the schema name serializes
/// concurrent runners across pods: the second pod blocks until the first commits, then sees the
/// migration already applied and does nothing. The lock is released automatically at
/// transaction end, so it can never leak.
/// </para>
/// <para>
/// <b>Idempotency:</b> applied versions are recorded in <c>{schema}.__migrations</c>; a second run
/// is a no-op. The schema name is validated fail-fast (<see cref="SchemaName.Validate"/>) before
/// any interpolation into an identifier (audit S1, R3, security scan #7); migration bookkeeping
/// values travel as <c>NpgsqlParameter</c>s.
/// </para>
/// </summary>
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification =
        "The only interpolated identifier is the schema name, validated fail-fast against " +
        "^[a-z_][a-z0-9_]{0,62}$ by SchemaName.Validate in the constructor before any use — the " +
        "sanctioned identifier-interpolation exception (audit S1, R3, security scan #7; " +
        "CONSTITUTION §2 FORBIDDEN). All non-identifier values travel as NpgsqlParameter.")]
public sealed class MigrationRunner
{
    private const string ResourceMarker = ".Migrations.Sql.";
    private const string SchemaToken = "{schema}";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly IReadOnlyList<Migration> _migrations;
    private readonly ILogger<MigrationRunner>? _logger;

    /// <summary>
    /// Creates a runner that discovers its migrations from this assembly's embedded SQL resources.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source; connections must have DDL privileges (dev / CD migration step).</param>
    /// <param name="options">Backend options; <see cref="ActaPostgresOptions.SchemaName"/> is validated fail-fast here.</param>
    /// <param name="logger">Optional logger for migration-applied / schema-up-to-date diagnostics.</param>
    public MigrationRunner(
        NpgsqlDataSource dataSource,
        ActaPostgresOptions options,
        ILogger<MigrationRunner>? logger = null)
        : this(dataSource, options, DiscoverEmbeddedMigrations(), logger)
    {
    }

    /// <summary>
    /// Test/extension seam (OQ-1): construct a runner over an explicit migration list — used to
    /// exercise transactional-DDL rollback without shipping a broken migration in production.
    /// </summary>
    internal MigrationRunner(
        NpgsqlDataSource dataSource,
        ActaPostgresOptions options,
        IReadOnlyList<Migration> migrations,
        ILogger<MigrationRunner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(migrations);

        _dataSource = dataSource;
        _schema = SchemaName.Validate(options.SchemaName);
        _migrations = migrations;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the schema and every discovered migration are applied, in one transaction, guarded by
    /// a per-schema advisory lock. Idempotent: already-applied migrations are skipped.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 1) Serialize concurrent runners across pods (D2). Transaction-scoped -> auto-released on
        //    COMMIT/ROLLBACK, so the lock can never leak. Key namespaced by schema so two Acta
        //    instances (or apps) on the same database do not collide.
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction))
        {
            lockCmd.Parameters.AddWithValue("key", $"{_schema}:__migrations");
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 2) Bootstrap schema + tracking table (idempotent) before reading the applied set.
        await ExecuteNonQueryAsync(connection, transaction,
            $"CREATE SCHEMA IF NOT EXISTS {_schema}", ct).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, transaction,
            $"""
             CREATE TABLE IF NOT EXISTS {_schema}.__migrations (
                 version    bigint PRIMARY KEY,
                 name       text NOT NULL,
                 applied_at timestamptz NOT NULL DEFAULT now()
             )
             """, ct).ConfigureAwait(false);

        // 3) Read the already-applied versions.
        var applied = new HashSet<long>();
        await using (var selectCmd = new NpgsqlCommand(
            $"SELECT version FROM {_schema}.__migrations", connection, transaction))
        await using (var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                applied.Add(reader.GetInt64(0));
            }
        }

        // 4) Apply pending migrations in version order, recording each.
        var pending = _migrations.Where(m => !applied.Contains(m.Version))
                                 .OrderBy(m => m.Version)
                                 .ToList();

        if (pending.Count == 0)
        {
            _logger?.LogInformation("Schema {Schema} is up to date; no migrations to apply.", _schema);
        }

        foreach (var migration in pending)
        {
            var sql = migration.Sql.Replace(SchemaToken, _schema, StringComparison.Ordinal);
            await ExecuteNonQueryAsync(connection, transaction, sql, ct).ConfigureAwait(false);

            await using var recordCmd = new NpgsqlCommand(
                $"INSERT INTO {_schema}.__migrations (version, name) VALUES (@version, @name)",
                connection, transaction);
            recordCmd.Parameters.AddWithValue("version", migration.Version);
            recordCmd.Parameters.AddWithValue("name", migration.Name);
            await recordCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _logger?.LogInformation(
                "Applied migration {Version} {Name} to schema {Schema}.",
                migration.Version, migration.Name, _schema);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Discovers embedded migration resources of this assembly, ordered by version. Throws when the
    /// set is empty — an empty set means the package was built without its SQL resources, a
    /// configuration error worth failing fast on.
    /// </summary>
    private static IReadOnlyList<Migration> DiscoverEmbeddedMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var migrations = new List<Migration>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            var markerIndex = resourceName.IndexOf(ResourceMarker, StringComparison.Ordinal);
            if (markerIndex < 0 || !resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = resourceName[(markerIndex + ResourceMarker.Length)..];
            var digits = new string([.. fileName.TakeWhile(char.IsAsciiDigit)]);
            if (digits.Length == 0)
            {
                continue;
            }

            var version = long.Parse(digits, CultureInfo.InvariantCulture);
            var name = fileName[..^".sql".Length];

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' could not be opened.");
            using var streamReader = new StreamReader(stream);
            var sql = streamReader.ReadToEnd();

            migrations.Add(new Migration(version, name, sql));
        }

        if (migrations.Count == 0)
        {
            throw new InvalidOperationException(
                "No embedded migrations found. Expected resources matching " +
                $"'*{ResourceMarker}NNNN_*.sql' in assembly '{assembly.GetName().Name}'.");
        }

        return [.. migrations.OrderBy(m => m.Version)];
    }
}
