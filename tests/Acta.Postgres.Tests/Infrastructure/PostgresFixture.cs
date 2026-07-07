using Npgsql;

using Testcontainers.PostgreSql;

using Xunit;

namespace Acta.Postgres.Tests.Infrastructure;

/// <summary>
/// Shared Testcontainers PostgreSQL fixture for the whole <c>Acta.Postgres.Tests</c> suite
/// (migrations, contract parity, coordination). One container is started for the collection
/// (<see cref="PostgresCollection"/>) because container startup is expensive; test isolation is
/// achieved by giving each test its own schema name rather than its own container.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // The image is pinned explicitly via WithImage for test determinism; the parameterless builder
    // ctor is obsolete-flagged but the image-parameter replacement API is still in flux across
    // Testcontainers 4.x point releases, so we keep the stable documented fluent form.
#pragma warning disable CS0618
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .Build();
#pragma warning restore CS0618

    private NpgsqlDataSource? _dataSource;

    /// <summary>The shared data source pointing at the running container (DDL-privileged superuser).</summary>
    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>
    /// Produces a fresh, allow-list-valid schema name unique to one test, so migration tests run
    /// against a clean schema without spinning a new container (and exercise schema templating).
    /// </summary>
    public static string NewSchemaName() => "acta_test_" + Guid.NewGuid().ToString("N")[..16];

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}
