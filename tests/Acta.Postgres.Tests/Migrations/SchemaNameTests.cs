using Acta.Postgres.Configuration;

using Xunit;

namespace Acta.Postgres.Tests.Migrations;

/// <summary>
/// Unit tests for <see cref="SchemaName.Validate"/> — the fail-fast allow-list that is the sole
/// gate before a schema name is interpolated into a SQL identifier (audit S1, R3, security scan
/// #7). No database required.
/// </summary>
public sealed class SchemaNameTests
{
    [Theory]
    [InlineData("acta")]
    [InlineData("acta_test_01")]
    [InlineData("a")]
    [InlineData("_private")]
    [InlineData("acta_test_0123456789abcdef")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789_abcdefghijklmnopqrstuvwxyz")] // 63 chars (max)
    public void Validate_ValidName_ReturnsSameName(string schemaName)
    {
        SchemaName.Validate(schemaName).Should().Be(schemaName);
    }

    [Theory]
    [InlineData("")]                    // empty
    [InlineData("Acta")]                // upper-case start
    [InlineData("acTa")]                // upper-case inside
    [InlineData("1acta")]              // digit start
    [InlineData("acta-x")]             // hyphen
    [InlineData("acta.public")]        // dot (schema-qualified injection)
    [InlineData("acta;drop table x")]  // statement injection
    [InlineData("acta schema")]        // whitespace
    [InlineData("\"acta\"")]           // quoting
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789_abcdefghijklmnopqrstuvwxyz1")] // 64 chars (too long)
    public void Validate_InvalidName_ThrowsArgumentException(string schemaName)
    {
        var act = () => SchemaName.Validate(schemaName);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*^[a-z_][a-z0-9_]*");
    }
}
