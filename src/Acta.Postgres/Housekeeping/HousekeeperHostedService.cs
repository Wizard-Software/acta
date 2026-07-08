using Acta.Postgres.Configuration;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Acta.Postgres.Housekeeping;

/// <summary>
/// Drives <see cref="Housekeeper.SweepAsync"/> on the <see cref="HousekeepingOptions.Interval"/>
/// cadence (04-data §3.6): a <see cref="BackgroundService"/> registered by <c>AddActaPostgres(...)</c>.
/// The sweep is single-active on the <c>{schema}:housekeeping</c> advisory lock, so running this
/// hosted service on every pod is safe — exactly one pod purges per tick, the rest skip.
/// <para>
/// A non-positive <see cref="HousekeepingOptions.Interval"/> disables the loop entirely (a deliberate
/// host decision — table cleanup then becomes an operational responsibility, 04-data §3.6): the service
/// logs a startup notice and returns without ever sweeping. A sweep that throws is logged and retried
/// on the next tick — a transient database error never tears the host down.
/// </para>
/// </summary>
internal sealed class HousekeeperHostedService : BackgroundService
{
    private readonly Housekeeper _housekeeper;
    private readonly ActaPostgresOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HousekeeperHostedService>? _logger;

    /// <param name="housekeeper">The single-active sweep this service drives.</param>
    /// <param name="options">Backend options carrying <see cref="ActaPostgresOptions.Housekeeping"/> (interval + retention).</param>
    /// <param name="timeProvider">The clock for the periodic tick; <see langword="null"/> resolves to <see cref="TimeProvider.System"/> (mirrors <c>ProjectionDaemon</c>).</param>
    /// <param name="logger">Optional lifecycle/diagnostics logger.</param>
    /// <exception cref="ArgumentNullException"><paramref name="housekeeper"/> or <paramref name="options"/> is null.</exception>
    public HousekeeperHostedService(
        Housekeeper housekeeper,
        ActaPostgresOptions options,
        TimeProvider? timeProvider = null,
        ILogger<HousekeeperHostedService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(housekeeper);
        ArgumentNullException.ThrowIfNull(options);

        _housekeeper = housekeeper;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.Housekeeping.Interval;
        if (interval <= TimeSpan.Zero)
        {
            _logger?.LogInformation(
                "Housekeeping disabled (Interval <= 0) for schema {Schema} — auxiliary-table cleanup is the host's operational responsibility (04-data §3.6).",
                _options.SchemaName);
            return;
        }

        _logger?.LogInformation(
            "Housekeeping loop started for schema {Schema} at interval {Interval}.", _options.SchemaName, interval);

        using var timer = new PeriodicTimer(interval, _timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _housekeeper.SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a transient sweep failure crash the host — log and retry next tick.
                _logger?.LogError(ex, "Housekeeping sweep failed for schema {Schema}; retrying next tick.", _options.SchemaName);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
