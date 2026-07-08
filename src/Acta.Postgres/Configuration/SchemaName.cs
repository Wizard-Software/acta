using System.Text.RegularExpressions;

namespace Acta.Postgres.Configuration;

/// <summary>
/// Fail-fast validation of the PostgreSQL schema name before it is interpolated into DDL/DML.
/// <para>
/// SQL identifiers (schema name, table prefixes) cannot travel as <c>NpgsqlParameter</c> values —
/// they are interpolated into the command text. CONSTITUTION §2 FORBIDDEN therefore permits that
/// interpolation <b>only</b> for a value validated fail-fast against a strict allow-list
/// (audit S1, revision R3, security scan #7): a value outside the pattern is a configuration
/// error, never silently sanitized. The allow-list is <c>^[a-z_][a-z0-9_]{0,62}$</c> — lower-case
/// ASCII letters, digits and underscores, starting with a letter or underscore, at most 63 bytes
/// (the PostgreSQL identifier length limit).
/// </para>
/// </summary>
public static partial class SchemaName
{
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex AllowList();

    /// <summary>
    /// Returns <paramref name="schemaName"/> unchanged when it matches the allow-list
    /// <c>^[a-z_][a-z0-9_]{0,62}$</c>; otherwise throws. The returned value is the only string safe
    /// to interpolate into a schema-qualified identifier.
    /// </summary>
    /// <param name="schemaName">The candidate schema name (from <see cref="ActaPostgresOptions.SchemaName"/>).</param>
    /// <returns>The validated schema name (identical to the input).</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaName"/> is <see langword="null"/>, empty, or does not match the
    /// allow-list — an unrecoverable configuration error surfaced at startup, never at query time.
    /// </exception>
    public static string Validate(string schemaName)
    {
        if (string.IsNullOrEmpty(schemaName) || !AllowList().IsMatch(schemaName))
        {
            throw new ArgumentException(
                $"Invalid schema name '{schemaName}'. It must match ^[a-z_][a-z0-9_]{{0,62}}$ " +
                "(lower-case ASCII letter/underscore start, then letters/digits/underscores, max 63 chars).",
                nameof(schemaName));
        }

        return schemaName;
    }
}
