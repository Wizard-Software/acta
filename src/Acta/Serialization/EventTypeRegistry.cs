using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Acta.Serialization;

/// <summary>
/// Maps logical event-type names (persisted, CLR-independent, e.g. <c>"OrderPlaced"</c>) to CLR types and
/// their current payload schema version, and back. Populated once at host startup (inside the
/// <c>AddActa</c> configuration) and read-only afterward, so runtime lookups are lock-free dictionary hits
/// with no reflection: each registered type gets a cached (de)serialization delegate built once at
/// registration (see 05-implementation §2 — "no reflection in the hot path (delegate cache)").
/// <para>
/// Security: the registry deserializes ONLY to pre-registered types (an allow-list); a name outside the
/// registry throws <see cref="UnknownEventTypeException"/> rather than loading an arbitrary, input-controlled
/// type. It never enables polymorphic <c>System.Text.Json</c> configuration on its own.
/// </para>
/// <para>
/// Multi-pod behavior class: safe-by-design — process-local, immutable after startup, identical on every
/// pod (derived deterministically from code/configuration); no shared state and no coordination.
/// </para>
/// </summary>
public sealed class EventTypeRegistry
{
    private sealed class Registration
    {
        public required string EventType { get; init; }
        public required int SchemaVersion { get; init; }
        public required Type ClrType { get; init; }
        public required Func<ReadOnlyMemory<byte>, JsonSerializerOptions, object> Deserialize { get; init; }
        public required Func<object, JsonSerializerOptions, byte[]> Serialize { get; init; }
    }

    private readonly Dictionary<string, Registration> _byEventType = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, Registration> _byClrType = new();

    /// <summary>The number of registered event types.</summary>
    public int Count => _byEventType.Count;

    /// <summary>
    /// Registers a CLR event type <typeparamref name="T"/> under a logical event-type name and a schema
    /// version. Fail-fast: throws on a null/blank name, a schema version below 1, or a duplicate name or
    /// duplicate CLR type. Registration runs inside host-startup configuration, so this is the "validate the
    /// registry at host startup" gate (03-contracts §3).
    /// </summary>
    /// <typeparam name="T">The CLR event type.</typeparam>
    /// <param name="eventType">The logical, persisted event-type name (case-sensitive, ordinal).</param>
    /// <param name="schemaVersion">The current payload schema version (must be &gt;= 1). Defaults to 1.</param>
    /// <returns>This registry, to allow fluent chaining.</returns>
    /// <exception cref="ArgumentException">The name is null/blank, or the name or CLR type is already registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The schema version is below 1.</exception>
    public EventTypeRegistry Register<T>(string eventType, int schemaVersion = 1)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("Event type name must not be null or blank.", nameof(eventType));
        if (schemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Schema version must be >= 1.");

        var clrType = typeof(T);
        if (_byClrType.TryGetValue(clrType, out var existingByType))
            throw new ArgumentException(
                $"CLR type '{clrType}' is already registered as event type '{existingByType.EventType}'.",
                nameof(eventType));
        if (_byEventType.ContainsKey(eventType))
            throw new ArgumentException($"Event type '{eventType}' is already registered.", nameof(eventType));

        var registration = new Registration
        {
            EventType = eventType,
            SchemaVersion = schemaVersion,
            ClrType = clrType,
            Deserialize = static (payload, options) => JsonSerializer.Deserialize<T>(payload.Span, options)!,
            Serialize = static (@event, options) => JsonSerializer.SerializeToUtf8Bytes((T)@event, options),
        };
        _byEventType.Add(eventType, registration);
        _byClrType.Add(clrType, registration);
        return this;
    }

    /// <summary>
    /// Registers a CLR event type <typeparamref name="T"/> using <c>typeof(T).Name</c> as the logical
    /// event-type name and schema version 1.
    /// </summary>
    /// <typeparam name="T">The CLR event type.</typeparam>
    /// <returns>This registry, to allow fluent chaining.</returns>
    public EventTypeRegistry Register<T>() => Register<T>(typeof(T).Name, schemaVersion: 1);

    /// <summary>Resolves a logical event-type name to its registered CLR type (read/deserialize path).</summary>
    /// <param name="eventType">The logical event-type name.</param>
    /// <returns>The registered CLR type.</returns>
    /// <exception cref="UnknownEventTypeException">No CLR type is registered for the name.</exception>
    public Type ResolveClrType(string eventType)
        => _byEventType.TryGetValue(eventType, out var registration)
            ? registration.ClrType
            : throw new UnknownEventTypeException(eventType);

    /// <summary>Tries to resolve a logical event-type name to its registered CLR type.</summary>
    /// <param name="eventType">The logical event-type name.</param>
    /// <param name="clrType">The registered CLR type when found; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a registration exists; otherwise <c>false</c>.</returns>
    public bool TryResolveClrType(string eventType, [NotNullWhen(true)] out Type? clrType)
    {
        if (_byEventType.TryGetValue(eventType, out var registration))
        {
            clrType = registration.ClrType;
            return true;
        }

        clrType = null;
        return false;
    }

    /// <summary>Resolves a CLR event type to its logical name and current schema version (write/serialize path).</summary>
    /// <param name="clrType">The CLR event type.</param>
    /// <returns>The registered logical event-type name and its current schema version.</returns>
    /// <exception cref="UnknownEventTypeException">The CLR type is not registered.</exception>
    public (string EventType, int SchemaVersion) ResolveEventType(Type clrType)
        => _byClrType.TryGetValue(clrType, out var registration)
            ? (registration.EventType, registration.SchemaVersion)
            : throw new UnknownEventTypeException(clrType);

    /// <summary>Tries to resolve a CLR event type to its logical name and current schema version.</summary>
    /// <param name="clrType">The CLR event type.</param>
    /// <param name="eventType">The registered logical name when found; otherwise <c>null</c>.</param>
    /// <param name="schemaVersion">The registered schema version when found; otherwise <c>0</c>.</param>
    /// <returns><c>true</c> when a registration exists; otherwise <c>false</c>.</returns>
    public bool TryResolveEventType(Type clrType, [NotNullWhen(true)] out string? eventType, out int schemaVersion)
    {
        if (_byClrType.TryGetValue(clrType, out var registration))
        {
            eventType = registration.EventType;
            schemaVersion = registration.SchemaVersion;
            return true;
        }

        eventType = null;
        schemaVersion = 0;
        return false;
    }

    /// <summary>
    /// Deserializes a JSON payload to the CLR type registered under <paramref name="eventType"/>, using the
    /// cached per-type delegate (no reflection on the hot path).
    /// </summary>
    /// <param name="eventType">The logical event-type name of the payload.</param>
    /// <param name="payload">The UTF-8 JSON payload.</param>
    /// <param name="options">The serializer options to use.</param>
    /// <returns>The deserialized event instance.</returns>
    /// <exception cref="UnknownEventTypeException">No CLR type is registered for the name.</exception>
    public object Deserialize(string eventType, ReadOnlyMemory<byte> payload, JsonSerializerOptions options)
        => _byEventType.TryGetValue(eventType, out var registration)
            ? registration.Deserialize(payload, options)
            : throw new UnknownEventTypeException(eventType);

    /// <summary>
    /// Serializes an event instance to a UTF-8 JSON payload using the cached per-type delegate for its
    /// runtime type (no reflection on the hot path).
    /// </summary>
    /// <param name="event">The event instance to serialize.</param>
    /// <param name="options">The serializer options to use.</param>
    /// <returns>The UTF-8 JSON payload.</returns>
    /// <exception cref="UnknownEventTypeException">The event's CLR type is not registered.</exception>
    public byte[] SerializePayload(object @event, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var clrType = @event.GetType();
        return _byClrType.TryGetValue(clrType, out var registration)
            ? registration.Serialize(@event, options)
            : throw new UnknownEventTypeException(clrType);
    }
}
