using Npgsql;

namespace Acta.Postgres.Store;

/// <summary>
/// The single authoring point for the tenant-scoping filter in <c>Acta.Postgres</c> DML (ADR-007
/// MUST/FORBIDDEN, ADR-016; 04-data §5). Every tenant-scoped read/write composes its <c>WHERE</c>
/// body from <see cref="ScopedWhere"/> and binds its scope-key parameter through
/// <see cref="BindTenant"/>, so the <c>tenant_id =</c> filter literal and the <c>@tenant</c> parameter
/// name exist in exactly one place: a future query cannot silently omit the tenant filter, and a grep
/// for the inline literal outside this type returns zero hits (the enforceability check task 10.3
/// applies).
/// <para>
/// Task 7.7 introduces this minimal surface (the first <c>Acta.Postgres</c> component writing
/// tenant-scoped DML — the reservation and idempotency stores); task 10.3 extends it for the
/// coordination stores (checkpoints / leader election). It is deliberately small: it centralizes the
/// filter predicate and the parameter binding, nothing more. It never interpolates a value — residual
/// key predicates are supplied already-parameterized by the caller.
/// </para>
/// </summary>
internal static class TenantScopedSqlBuilder
{
    /// <summary>The canonical parameter name carrying the tenant scope key (bound by <see cref="BindTenant"/>).</summary>
    public const string TenantParameterName = "tenant";

    // The sole authored occurrence of the tenant-filter column/predicate. Kept private so no other
    // type can reference (or drift from) the literal — callers only ever get a fully-composed WHERE.
    private const string TenantColumn = "tenant_id";

    /// <summary>
    /// Builds a tenant-scoped <c>WHERE</c> body: the mandatory <c>tenant_id = @tenant</c> filter,
    /// AND-joined with the supplied <paramref name="keyPredicates"/> (already parameterized by the
    /// caller — this builder never interpolates values). This is the ONLY place the <c>tenant_id =</c>
    /// filter literal is authored (ADR-007/ADR-016 enforceability).
    /// </summary>
    /// <param name="keyPredicates">
    /// Residual, already-parameterized key predicates (e.g. <c>"scope = @scope"</c>) AND-appended
    /// after the tenant filter. May be empty for a tenant-only scope.
    /// </param>
    /// <returns>The composed <c>WHERE</c> body (without the leading <c>WHERE</c> keyword).</returns>
    public static string ScopedWhere(params string[] keyPredicates)
    {
        var tenantFilter = $"{TenantColumn} = @{TenantParameterName}";
        return keyPredicates is { Length: > 0 }
            ? tenantFilter + " AND " + string.Join(" AND ", keyPredicates)
            : tenantFilter;
    }

    /// <summary>
    /// Binds the tenant scope key onto <paramref name="command"/>, normalizing <see langword="null"/>
    /// to <c>''</c> (the single-tenant slot, matching the DDL <c>tenant_id text NOT NULL DEFAULT ''</c>
    /// and the tenant-aware primary keys; ADR-016). Every tenant-scoped command — including
    /// <c>INSERT … ON CONFLICT</c> whose scoping is structural (PK) rather than a <c>WHERE</c> filter —
    /// binds its scope key here so the null→'' normalization lives in one place.
    /// </summary>
    /// <param name="command">The command to bind the <c>@tenant</c> parameter onto.</param>
    /// <param name="tenantId">The tenant scope, or <see langword="null"/> for single-tenant.</param>
    public static void BindTenant(NpgsqlCommand command, string? tenantId)
        => command.Parameters.AddWithValue(TenantParameterName, tenantId ?? string.Empty);
}
