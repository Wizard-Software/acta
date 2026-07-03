using System.Text.Json;
using System.Text.Json.Serialization;

using Acta.Abstractions;

namespace Acta.Serialization;

/// <summary>
/// Orchestrates (de)serialization of events on top of the <see cref="EventTypeRegistry"/>
/// primitives, keeping the event payload and its <see cref="EventMetadata"/> strictly
/// separate (FR-10, Young's schema discipline).
/// <para>
/// Write path (<see cref="ToEventData"/>): stamps the logical <c>EventType</c>/<c>SchemaVersion</c>
/// from the registry, serializes the event instance to a JSON payload, and carries
/// <see cref="EventMetadata"/> alongside as a separate, typed field — metadata is never merged
/// into the payload bytes.
/// </para>
/// <para>
/// Read path (<see cref="ToSourcedEvent"/>): deserializes <see cref="StoredEvent.Payload"/> to a
/// CLR instance through the registry's allow-list (an unregistered <c>EventType</c> name throws
/// <see cref="UnknownEventTypeException"/> rather than loading an arbitrary, input-controlled
/// type) and assembles the resulting <see cref="SourcedEvent"/>.
/// </para>
/// <para>
/// Metadata (<see cref="SerializeMetadata"/>/<see cref="DeserializeMetadata"/>) is (de)serialized
/// as its own, independent JSON document, entirely outside the payload's upcasting path: the
/// shape of <see cref="EventMetadata"/> is frozen (ADR-011, ADR-017) and never passes through an
/// upcaster. A private, nested <see cref="UserRefJsonConverter"/> gives <see cref="UserRef"/> a
/// resilient, string-shaped wire representation.
/// </para>
/// <para>
/// Multi-pod behavior class: safe-by-design — process-local and stateless beyond a read-only
/// <see cref="EventTypeRegistry"/> and two <see cref="JsonSerializerOptions"/> instances that
/// become immutable after construction; identical behavior on every pod, no shared state and no
/// coordination.
/// </para>
/// </summary>
public sealed class EventSerializer
{
    /// <summary>
    /// Gives <see cref="UserRef"/> a resilient, string-shaped JSON representation instead of the
    /// default object shape System.Text.Json would otherwise produce for a single-property
    /// record struct.
    /// </summary>
    private sealed class UserRefJsonConverter : JsonConverter<UserRef>
    {
        /// <inheritdoc/>
        public override UserRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected a JSON string value for UserRef.");
            }

            return new UserRef(reader.GetString()!);
        }

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, UserRef value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }

    private readonly EventTypeRegistry _registry;
    private readonly JsonSerializerOptions _payloadOptions;
    private readonly JsonSerializerOptions _metadataOptions;

    /// <summary>
    /// Creates an <see cref="EventSerializer"/> bound to a registry and a set of serializer
    /// options. A private copy of <paramref name="options"/>, with a resilient
    /// <see cref="UserRef"/> converter added, is built once here for the metadata path; the
    /// caller's <paramref name="options"/> instance is never mutated (System.Text.Json freezes
    /// options after their first use).
    /// </summary>
    /// <param name="registry">The event-type registry used to (de)serialize payloads.</param>
    /// <param name="options">The serializer options used for the payload path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public EventSerializer(EventTypeRegistry registry, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        _registry = registry;
        _payloadOptions = options;
        _metadataOptions = new JsonSerializerOptions(options);
        _metadataOptions.Converters.Add(new UserRefJsonConverter());
    }

    /// <summary>
    /// Builds an <see cref="EventData"/> ready to append: stamps the logical
    /// <c>EventType</c>/<c>SchemaVersion</c> for <paramref name="event"/>'s CLR type from the
    /// registry, serializes <paramref name="event"/> to a JSON payload, and attaches
    /// <paramref name="metadata"/> as a separate, typed field. The payload bytes carry only the
    /// event's own fields — metadata never rides inside the payload JSON (FR-10).
    /// </summary>
    /// <param name="event">The event instance to serialize.</param>
    /// <param name="metadata">The causation metadata to associate with the event.</param>
    /// <param name="eventId">
    /// The append-time deduplication key (ADR-003); the caller — not this serializer — owns the
    /// policy for generating it.
    /// </param>
    /// <returns>The assembled <see cref="EventData"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="event"/> or <paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="UnknownEventTypeException"><paramref name="event"/>'s CLR type is not registered.</exception>
    public EventData ToEventData(object @event, EventMetadata metadata, Guid eventId)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(metadata);

        var (eventType, schemaVersion) = _registry.ResolveEventType(@event.GetType());
        var payload = _registry.SerializePayload(@event, _payloadOptions);
        return new EventData(eventId, eventType, schemaVersion, payload, metadata);
    }

    /// <summary>
    /// Deserializes <paramref name="stored"/>'s payload to a CLR instance through the registry's
    /// allow-list and assembles the resulting <see cref="SourcedEvent"/>.
    /// <para>
    /// Upcasting seam (Tier 1 — no upcasters registered yet): <see cref="SourcedEvent.Event"/> is
    /// defined as "the CLR instance after upcasting". In Tier 1 there is no upcaster chain, so
    /// <see cref="SourcedEvent.Event"/> is simply the payload deserialized to its current schema
    /// version. The insertion point for upcasting (group 7) is the gap between the registry's
    /// <c>Deserialize</c> call and the assembly of <see cref="SourcedEvent"/> below: a future
    /// version of this method gains an <c>UpcasterChain</c> dependency and an
    /// <c>chain.Upcast(...)</c> step inserted there, without changing this method's signature — a
    /// non-breaking evolution. <see cref="StoredEvent.Metadata"/> flows into
    /// <see cref="SourcedEvent.Raw"/> untouched by upcasting, preserving the FR-10 discipline on
    /// the read path as well.
    /// </para>
    /// </summary>
    /// <param name="stored">The stored event to deserialize.</param>
    /// <returns>The assembled <see cref="SourcedEvent"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stored"/> is <see langword="null"/>.</exception>
    /// <exception cref="UnknownEventTypeException"><paramref name="stored"/>'s <c>EventType</c> is not registered.</exception>
    public SourcedEvent ToSourcedEvent(StoredEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var @event = _registry.Deserialize(stored.EventType, stored.Payload, _payloadOptions);
        return new SourcedEvent(@event, stored);
    }

    /// <summary>
    /// Serializes <paramref name="metadata"/> to its own, independent UTF-8 JSON document —
    /// entirely outside the payload's upcasting path. <see cref="UserRef"/> is written as a plain
    /// JSON string via the private <see cref="UserRefJsonConverter"/>.
    /// </summary>
    /// <param name="metadata">The metadata to serialize.</param>
    /// <returns>The UTF-8 JSON bytes of <paramref name="metadata"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    public byte[] SerializeMetadata(EventMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return JsonSerializer.SerializeToUtf8Bytes(metadata, _metadataOptions);
    }

    /// <summary>
    /// Deserializes a UTF-8 JSON document produced by <see cref="SerializeMetadata"/> back into an
    /// <see cref="EventMetadata"/> instance.
    /// </summary>
    /// <param name="metadataJson">The UTF-8 JSON bytes to deserialize.</param>
    /// <returns>The deserialized <see cref="EventMetadata"/>.</returns>
    /// <exception cref="JsonException">
    /// <paramref name="metadataJson"/> is malformed, deserializes to <see langword="null"/> (e.g.
    /// the JSON literal <c>null</c>), or its <c>User</c> field is a non-string JSON token (a
    /// technical pseudonym is always a string on the wire).
    /// </exception>
    public EventMetadata DeserializeMetadata(ReadOnlyMemory<byte> metadataJson)
        => JsonSerializer.Deserialize<EventMetadata>(metadataJson.Span, _metadataOptions)
            ?? throw new JsonException("Metadata JSON deserialized to null.");
}
