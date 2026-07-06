using System.Text.Json;

using Acta.Serialization;

namespace Acta.Configuration;

/// <summary>
/// Configuration for <c>AddActa()</c> (composition root, MODULE-INTERFACES "Rejestracja DI").
/// <para>
/// Tier-1 shape (task 3.3, decision D1): this type carries only the configuration the Tier 1
/// composition root actually reads. Fields describing not-yet-existing Tier components
/// (<c>Snapshots</c>/<c>SnapshotPolicy</c>, <c>Daemon</c>/<c>ProjectionDaemonOptions</c>, and the
/// append-path payload/batch limits) are deliberately omitted here to avoid premature public
/// types with no reader (CONSTITUTION §2 ASK FIRST) — they arrive as non-breaking additive
/// properties in the Feature that introduces the component reading them.
/// </para>
/// <para>
/// <see cref="Events"/> and <see cref="SerializerOptions"/> are mutated only inside the
/// <c>configure</c> delegate passed to <c>AddActa</c>; both are read once by the composition
/// root's singleton factories and must be treated as effectively immutable afterward — mutating
/// either after the host has built its <see cref="System.IServiceProvider"/> has undefined effects
/// on already-resolved singletons.
/// </para>
/// </summary>
public sealed class ActaOptions
{
    /// <summary>
    /// The registry mapping logical event-type names to CLR types and schema versions. Starts
    /// empty; the host registers its event types here inside the <c>configure</c> delegate (e.g.
    /// <c>o.Events.Register&lt;OrderPlaced&gt;()</c>). Read-only after host startup — see the type
    /// remarks.
    /// </summary>
    public EventTypeRegistry Events { get; } = new();

    /// <summary>
    /// The <see cref="JsonSerializerOptions"/> used to (de)serialize event payloads (FR-10). Not
    /// used for <see cref="Acta.Abstractions.EventMetadata"/>, which always travels as its own,
    /// independent JSON document (see <see cref="EventSerializer"/>). Defaults to a fresh instance
    /// seeded with <see cref="JsonSerializerDefaults.Web"/>; the host may replace it entirely.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Configuration for the asynchronous projection daemon (task 5.2). Additive, non-breaking:
    /// this is the reader foreshadowed by the type remarks — the daemon (Feature 5) is the component
    /// that consumes it. Defaults to a fresh <see cref="ProjectionDaemonOptions"/> with the
    /// 03-contracts §2 defaults; validated fail-fast at host startup.
    /// </summary>
    public ProjectionDaemonOptions Daemon { get; set; } = new();
}
