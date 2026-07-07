using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.DependencyInjection;
using Acta.Postgres.Idempotency;
using Acta.Postgres.Migrations;
using Acta.Postgres.Reservations;
using Acta.Postgres.Store;

using Npgsql;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The Tier 2 composition root for the PostgreSQL backend (task 7.9, MODULE-INTERFACES
/// "Rejestracja DI"): swaps Acta's in-memory <see cref="IEventStore"/>, <see cref="IReservationStore"/>
/// and <see cref="IIdempotencyStore"/> for their PostgreSQL-backed counterparts, registers the shared
/// <see cref="NpgsqlDataSource"/> and <see cref="MigrationRunner"/>, and wires an optional
/// startup migration via <c>MigrationHostedService</c>.
/// <para>
/// <b>Composition model (D1).</b> Both overloads call <see cref="ActaServiceCollectionExtensions.AddActa"/>
/// first — idempotently registering the shared Tier 1 graph (<c>EventSerializer</c>,
/// <c>EventTypeRegistry</c>, the default <c>Func&lt;EventMetadata&gt;</c> factory,
/// <c>ICorrelationContextAccessor</c>, the open-generic <c>IAggregateRepository&lt;&gt;</c>) — and then
/// override the backend via <see cref="ServiceCollectionDescriptorExtensions.Replace"/>, not
/// <c>TryAdd</c>: a host may call <c>AddActa(o => o.Events.Register&lt;T&gt;())</c> before
/// <c>AddActaPostgres</c> purely to register event types, which already registers the in-memory
/// <see cref="IEventStore"/> — <c>Replace</c> unconditionally swaps it for <see cref="PostgresEventStore"/>
/// regardless of call order, while the host's registered event types survive (MS.DI
/// <c>Configure</c> delegates accumulate independently of registration order).
/// </para>
/// <para>
/// <b>Ownership of <see cref="NpgsqlDataSource"/> (D2, R3, security scan #8).</b> The connection-string
/// overload builds a new <see cref="NpgsqlDataSource"/> and registers it as a container-owned
/// singleton — MS.DI disposes it (it implements <see cref="IAsyncDisposable"/>) when the provider is
/// disposed. The <see cref="NpgsqlDataSource"/> overload registers the host-supplied instance
/// as-is: the container never disposes an instance it did not create, which is required for hosts
/// that need IAM/Managed Identity authentication, periodic credential rotation, or full control over
/// the connection pool.
/// </para>
/// <para>
/// <b>Migrations (D3).</b> A singleton <see cref="MigrationRunner"/> and a <c>MigrationHostedService</c>
/// (<see cref="IHostedService"/>) are always registered; whether the hosted service actually migrates
/// at startup is controlled by <see cref="ActaPostgresOptions.AutoMigrate"/> (default
/// <see langword="true"/> — see that property's remarks and 04-data §4/§4.1).
/// </para>
/// <para>
/// <b>Least-privilege DB roles (D4, 04-data §4.1).</b> This composition root does not create or grant
/// database roles — that is an operational, DBA-owned step. The binding production role model
/// (<c>acta_migrator</c> DDL-only, <c>acta_runtime</c> DML-only) ships as
/// <c>Roles/acta-least-privilege-roles.sql</c>, a packaged artifact never discovered or executed by
/// <see cref="MigrationRunner"/> (its embedded-resource marker is <c>.Migrations.Sql.</c>, not
/// <c>.Roles.</c>).
/// </para>
/// </summary>
public static class ActaPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers Acta's PostgreSQL backend, building a new <see cref="NpgsqlDataSource"/> from
    /// <paramref name="connectionString"/> and registering it as a container-owned singleton.
    /// </summary>
    /// <param name="services">The service collection to register the PostgreSQL backend into.</param>
    /// <param name="connectionString">
    /// The Npgsql connection string the data source is built from. Must not be
    /// <see langword="null"/>, empty, or consist only of whitespace.
    /// </param>
    /// <param name="configure">
    /// An optional delegate to configure <see cref="ActaPostgresOptions"/> — typically used to set
    /// <see cref="ActaPostgresOptions.SchemaName"/> and <see cref="ActaPostgresOptions.AutoMigrate"/>.
    /// </param>
    /// <returns><paramref name="services"/>, to allow fluent chaining.</returns>
    /// <remarks>
    /// <b>Connection pool sizing (06-cross-cutting §4).</b> Npgsql reads pool sizing from
    /// <paramref name="connectionString"/>'s <c>Maximum Pool Size</c> keyword (Npgsql default:
    /// <c>100</c>); there is no separate pool-size option on <see cref="ActaPostgresOptions"/>. Size
    /// the pool with <c>MaxPoolSize &gt;= projections-in-catch-up + host-append-parallelism + 2</c> —
    /// enough connections for every catch-up projection to poll concurrently, the host's own append
    /// concurrency, and headroom for migrations plus incidental pool churn.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="connectionString"/> is <see langword="null"/>, empty, or consists only of
    /// whitespace.
    /// </exception>
    public static IServiceCollection AddActaPostgres(
        this IServiceCollection services, string connectionString, Action<ActaPostgresOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Container-owned: built here, so MS.DI disposes it (IAsyncDisposable) with the provider.
        services.TryAddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());

        return AddActaPostgresCore(services, configure);
    }

    /// <summary>
    /// Registers Acta's PostgreSQL backend over a host-supplied <see cref="NpgsqlDataSource"/> —
    /// preferred in managed environments (Azure/AWS/GCP) that need IAM/Managed Identity
    /// authentication, periodic credential rotation, or full host control over the connection pool
    /// (R3, security scan #8).
    /// </summary>
    /// <param name="services">The service collection to register the PostgreSQL backend into.</param>
    /// <param name="dataSource">
    /// The host-owned data source every registered store and <see cref="MigrationRunner"/> resolve
    /// and open connections from. The container registers this exact instance and never disposes
    /// it — the host retains full lifecycle ownership.
    /// </param>
    /// <param name="configure">
    /// An optional delegate to configure <see cref="ActaPostgresOptions"/> — typically used to set
    /// <see cref="ActaPostgresOptions.SchemaName"/> and <see cref="ActaPostgresOptions.AutoMigrate"/>.
    /// </param>
    /// <returns><paramref name="services"/>, to allow fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="dataSource"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddActaPostgres(
        this IServiceCollection services, NpgsqlDataSource dataSource, Action<ActaPostgresOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        // Host-owned: register the supplied instance as-is (not a factory) so the container never
        // constructs — and therefore never disposes — it. A later call wins over an earlier
        // TryAddSingleton-registered, container-owned data source from the connection-string overload.
        services.AddSingleton(dataSource);

        return AddActaPostgresCore(services, configure);
    }

    /// <summary>
    /// The shared registration core for both overloads: Tier 1 graph, options, backend swap,
    /// migrations. Both public overloads have already validated <c>services</c> and their own
    /// data-source argument, and have registered the <see cref="NpgsqlDataSource"/> before calling
    /// this method.
    /// </summary>
    private static IServiceCollection AddActaPostgresCore(IServiceCollection services, Action<ActaPostgresOptions>? configure)
    {
        services.AddActa();

        var optionsBuilder = services.AddOptions<ActaPostgresOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // A plain, resolvable ActaPostgresOptions singleton for the stores/MigrationRunner, whose
        // constructors take the options directly rather than IOptions<ActaPostgresOptions>.
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<ActaPostgresOptions>>().Value);

        // Backend swap (D1): Replace, not TryAdd — unconditionally overrides AddActa's in-memory
        // registration regardless of whether AddActa ran before or as part of this call.
        services.Replace(ServiceDescriptor.Singleton<IEventStore>(sp => new PostgresEventStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ActaPostgresOptions>(),
            sp.GetService<TimeProvider>())));

        services.Replace(ServiceDescriptor.Singleton<IReservationStore>(sp => new PostgresReservationStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ActaPostgresOptions>(),
            sp.GetService<ILogger<PostgresReservationStore>>())));

        services.Replace(ServiceDescriptor.Singleton<IIdempotencyStore>(sp => new PostgresIdempotencyStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ActaPostgresOptions>(),
            sp.GetService<ILogger<PostgresIdempotencyStore>>())));

        services.TryAddSingleton(sp => new MigrationRunner(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<ActaPostgresOptions>(),
            sp.GetService<ILogger<MigrationRunner>>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MigrationHostedService>());

        return services;
    }
}
