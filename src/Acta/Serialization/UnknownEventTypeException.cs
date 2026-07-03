namespace Acta.Serialization;

/// <summary>
/// Thrown when an event type cannot be resolved against the <see cref="EventTypeRegistry"/> — either a
/// logical event-type name read from the store has no registered CLR type (read/deserialize path), or a
/// CLR event instance being written was never registered (write/serialize path). This is a configuration
/// error surfaced fail-fast (see 03-contracts §3): the registry only ever maps to pre-registered types, so
/// an unresolved type signals a missing <see cref="EventTypeRegistry.Register{T}(string, int)"/> call.
/// </summary>
public sealed class UnknownEventTypeException : Exception
{
    /// <summary>
    /// The unresolved identifier: the logical event-type name (read path) or the CLR type's full name
    /// (write path).
    /// </summary>
    public string EventType { get; }

    /// <summary>Creates the exception for an unknown logical event-type name (read/deserialize path).</summary>
    /// <param name="eventType">The logical event-type name that has no registered CLR type.</param>
    public UnknownEventTypeException(string eventType)
        : base($"No CLR type is registered for event type '{eventType}'.")
        => EventType = eventType;

    /// <summary>Creates the exception for a CLR type that was never registered (write/serialize path).</summary>
    /// <param name="clrType">The CLR event type that has no registration.</param>
    public UnknownEventTypeException(Type clrType)
        : base($"CLR type '{clrType}' is not registered as an event type.")
        => EventType = clrType.FullName ?? clrType.Name;
}
