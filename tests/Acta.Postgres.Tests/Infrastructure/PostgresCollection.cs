using Xunit;

namespace Acta.Postgres.Tests.Infrastructure;

/// <summary>
/// xUnit collection definition sharing one <see cref="PostgresFixture"/> (a single Testcontainers
/// PostgreSQL container) across every test class tagged <c>[Collection("postgres")]</c> — the
/// migration, contract-parity and coordination suites of Feature 7.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(PostgresCollection.Name)]</c>.</summary>
    public const string Name = "postgres";
}
