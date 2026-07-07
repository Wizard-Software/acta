using System.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Acta.Abstractions;
using Acta.Postgres.Configuration;
using Acta.Postgres.Idempotency;
using Acta.Postgres.Reservations;
using Acta.Postgres.Store;
using Acta.Postgres.Subscriptions;
using Acta.Postgres.Tests.Infrastructure;
using Acta.Tests.TestSupport;

using Npgsql;

using Xunit;

namespace Acta.Postgres.Tests.DependencyInjection;

/// <summary>
/// Integration tests for <c>AddActaPostgres(...)</c> (task 7.9, MODULE-INTERFACES "Rejestracja
/// DI") against real PostgreSQL (Testcontainers): both overloads' backend registration,
/// <see cref="NpgsqlDataSource"/> ownership, <see cref="ActaPostgresOptions.AutoMigrate"/> startup
/// behavior, options wiring, idempotent re-registration, an end-to-end round trip through a
/// resolved <see cref="IAggregateRepository{CounterAggregate}"/>, and argument guards.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AddActaPostgresTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PostgresFixture _fixture = fixture;

    [Fact]
    public void AddActaPostgres_ConnectionStringOverload_RegistersResolvablePostgresBackendTypes()
    {
        using var provider = BuildProvider(s => s.AddActaPostgres(
            _fixture.ConnectionString, o => o.SchemaName = PostgresFixture.NewSchemaName()));

        provider.GetRequiredService<IEventStore>().Should().BeOfType<PostgresEventStore>();
        provider.GetRequiredService<ISubscriptionSource>().Should().BeOfType<PostgresSubscriptionSource>();
        provider.GetRequiredService<IReservationStore>().Should().BeOfType<PostgresReservationStore>();
        provider.GetRequiredService<IIdempotencyStore>().Should().BeOfType<PostgresIdempotencyStore>();
    }

    [Fact]
    public async Task AddActaPostgres_DataSourceOverload_RegistersSuppliedInstance_NotDisposedByProvider()
    {
        var provider = BuildProvider(s => s.AddActaPostgres(
            _fixture.DataSource, o => o.SchemaName = PostgresFixture.NewSchemaName()));

        provider.GetRequiredService<NpgsqlDataSource>().Should().BeSameAs(_fixture.DataSource);

        await provider.DisposeAsync();

        // Host-owned (D2): the fixture's data source must still be usable after the provider —
        // which never constructed it — is gone. A container-owned data source would be disposed
        // here and this connection attempt would throw ObjectDisposedException.
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        connection.State.Should().Be(ConnectionState.Open);
    }

    [Fact]
    public async Task AddActaPostgres_AutoMigrateTrue_HostedServiceStartAsync_AppliesMigrationsToConfiguredSchema()
    {
        var schema = PostgresFixture.NewSchemaName();
        using var provider = BuildProvider(s => s.AddActaPostgres(
            _fixture.ConnectionString, o => { o.SchemaName = schema; o.AutoMigrate = true; }));

        await StartAllHostedServicesAsync(provider);

        (await TableExistsAsync(schema, "streams")).Should().BeTrue();
    }

    [Fact]
    public async Task AddActaPostgres_AutoMigrateFalse_HostedServiceStartAsync_DoesNotMigrate_AndLogsWarning()
    {
        var schema = PostgresFixture.NewSchemaName();
        var recorder = new RecordingLoggerProvider();
        using var provider = BuildProvider(
            s => s.AddActaPostgres(
                _fixture.ConnectionString, o => { o.SchemaName = schema; o.AutoMigrate = false; }),
            recorder);

        await StartAllHostedServicesAsync(provider);

        (await TableExistsAsync(schema, "streams")).Should().BeFalse();
        recorder.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("AutoMigrate is disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddActaPostgres_ConfigureDelegate_CustomSchemaName_ReachesRegisteredReservationStore()
    {
        var schema = PostgresFixture.NewSchemaName();
        using var provider = BuildProvider(s => s.AddActaPostgres(
            _fixture.ConnectionString, o => o.SchemaName = schema));

        await StartAllHostedServicesAsync(provider); // AutoMigrate=true (default) migrates the CUSTOM schema.

        var reservationStore = provider.GetRequiredService<IReservationStore>();

        // If the configure delegate's custom schema hadn't reached PostgresReservationStore's own
        // constructor, this would either fault (targeting a nonexistent schema) or silently write
        // into the default "acta" schema instead of the one migrated above.
        (await reservationStore.TryReserveAsync("email", "custom-schema@acta.io", "owner", TimeSpan.FromMinutes(5), ct: Ct))
            .Should().BeTrue();
    }

    [Fact]
    public void AddActaPostgres_CalledTwice_RegistersExactlyOneEventStoreAndHostedServiceDescriptor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        var schema = PostgresFixture.NewSchemaName();
        services.AddActaPostgres(_fixture.ConnectionString, o => o.SchemaName = schema);
        services.AddActaPostgres(_fixture.ConnectionString, o => o.SchemaName = schema);

        services.Should().ContainSingle(d => d.ServiceType == typeof(IEventStore));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IHostedService));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEventStore>().Should().BeOfType<PostgresEventStore>();
    }

    [Fact]
    public async Task AddActaPostgres_EndToEnd_AppendAndReadRoundTripsThroughResolvedAggregateRepository()
    {
        var schema = PostgresFixture.NewSchemaName();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddActaPostgres(_fixture.ConnectionString, o => o.SchemaName = schema);
        services.AddActa(o => o.Events.Register<Incremented>()); // Tier-1 event-type registration.

        using var provider = services.BuildServiceProvider();
        await StartAllHostedServicesAsync(provider); // AutoMigrate=true (default) creates the schema/tables.

        var repository = provider.GetRequiredService<IAggregateRepository<CounterAggregate>>();

        var session = await repository.FetchForWritingAsync("counter-pg-dogfood", Ct);
        session.Aggregate.AssignId("counter-pg-dogfood");
        session.Aggregate.Increment();
        await repository.SaveAsync(session.Aggregate, session.ReadVersion, Ct);

        var loaded = await repository.GetByIdAsync("counter-pg-dogfood", Ct);

        loaded.Should().NotBeNull();
        loaded!.Applied.Should().Be(1);
    }

    [Fact]
    public void AddActaPostgres_ConnectionStringOverload_NullOrWhiteSpaceConnectionString_ThrowsArgumentException()
    {
        var services = new ServiceCollection();

        Invoking(() => services.AddActaPostgres("   ")).Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("connectionString");
    }

    [Fact]
    public void AddActaPostgres_DataSourceOverload_NullDataSource_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        NpgsqlDataSource? dataSource = null;

        Invoking(() => services.AddActaPostgres(dataSource!)).Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("dataSource");
    }

    [Fact]
    public void AddActaPostgres_NullServices_BothOverloads_ThrowArgumentNullException()
    {
        IServiceCollection? services = null;

        Invoking(() => services!.AddActaPostgres("Host=localhost")).Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("services");
        Invoking(() => services!.AddActaPostgres(_fixture.DataSource)).Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("services");
    }

    // --- fixtures & helpers ------------------------------------------------------------------

    private static ServiceProvider BuildProvider(
        Action<IServiceCollection> registerBackend, ILoggerProvider? loggerProvider = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerProvider is null
            ? NullLoggerFactory.Instance
            : new SingleProviderLoggerFactory(loggerProvider));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        registerBackend(services);

        return services.BuildServiceProvider();
    }

    private static async Task StartAllHostedServicesAsync(ServiceProvider provider)
    {
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(Ct);
        }
    }

    private async Task<bool> TableExistsAsync(string schema, string table)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct)) > 0;
    }

    /// <summary>Captures every log entry written through it — used to assert the AutoMigrate=false startup warning.</summary>
    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                {
                    owner._entries.Add(new LogEntry(logLevel, formatter(state, exception)));
                }
            }
        }
    }

    /// <summary>Forwards every category to one pre-built <see cref="ILoggerProvider"/> (no filtering infrastructure needed for a test double).</summary>
    private sealed class SingleProviderLoggerFactory(ILoggerProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider _)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}
