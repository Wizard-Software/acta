using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Acta.Postgres.Configuration;
using Acta.Postgres.Migrations;

namespace Acta.Postgres.DependencyInjection;

/// <summary>
/// Applies the <see cref="ActaPostgresOptions.AutoMigrate"/> decision at host startup (task 7.9,
/// D-V2): registered by <c>AddActaPostgres(...)</c> as an <see cref="IHostedService"/> alongside
/// <see cref="MigrationRunner"/>.
/// <para>
/// When <see cref="ActaPostgresOptions.AutoMigrate"/> is <see langword="true"/> (the default —
/// intended for development, 04-data §4), <see cref="StartAsync"/> logs an informational notice and
/// runs <see cref="MigrationRunner.MigrateAsync"/>, blocking host startup until the schema is
/// up to date. When it is <see langword="false"/> (recommended for production — migrations run as a
/// dedicated CD-pipeline step under the <c>acta_migrator</c> role, 04-data §4.1),
/// <see cref="StartAsync"/> does not migrate and instead logs a startup <see cref="LogLevel.Warning"/>
/// pointing at that CD step, because a production connection string is expected to carry only the
/// least-privilege <c>acta_runtime</c> role (DML-only, no DDL) and an accidental DDL-privileged
/// connection string combined with <c>AutoMigrate=false</c> would otherwise migrate silently.
/// </para>
/// </summary>
internal sealed class MigrationHostedService : IHostedService
{
    private readonly MigrationRunner _runner;
    private readonly IOptions<ActaPostgresOptions> _options;
    private readonly ILogger<MigrationHostedService>? _logger;

    /// <param name="runner">The migration runner this service drives at startup.</param>
    /// <param name="options">The resolved <see cref="ActaPostgresOptions"/>, read for <see cref="ActaPostgresOptions.AutoMigrate"/>.</param>
    /// <param name="logger">Optional logger for the startup migration/warning notice.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runner"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public MigrationHostedService(
        MigrationRunner runner,
        IOptions<ActaPostgresOptions> options,
        ILogger<MigrationHostedService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);

        _runner = runner;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Value.AutoMigrate)
        {
            _logger?.LogInformation(
                "AutoMigrate is enabled for schema {Schema} — applying schema migrations at startup (dev; see 04-data §4).",
                _options.Value.SchemaName);
            await _runner.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger?.LogWarning(
                "AutoMigrate is disabled — schema migrations must be applied out-of-band by the acta_migrator role in the CD pipeline; see 04-data §4.1.");
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
