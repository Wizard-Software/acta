using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

using Microsoft.Extensions.Logging;

using Acta.Abstractions;
using Acta.Diagnostics;
using Acta.Serialization;

namespace Acta.Projections.Inline;

/// <summary>
/// Default, in-memory dispatcher for <see cref="IProjection{TEvent}"/> (task 4.1, MODULE-INTERFACES
/// Grupa 5): given a batch of freshly appended <see cref="StoredEvent"/>s, pre-filters by
/// <see cref="StoredEvent.EventType"/> through the injected <see cref="EventTypeRegistry"/>,
/// deserializes only events that have at least one matching registered projection (through the
/// injected <see cref="EventSerializer"/>), and calls <see cref="IProjection{TEvent}.ApplyAsync"/>
/// for every match, in ascending <see cref="StoredEvent.GlobalPosition"/> order, guarded by a
/// per-projection idempotency watermark.
/// <para>
/// <b>Scope (task 4.1).</b> This runner is a self-contained, unit-testable component. It is
/// designed to be invoked from the append flow ("inline"), but wiring it into
/// <c>InMemoryEventStore.AppendAsync</c> is explicitly OUT of this task's scope: that store
/// publishes state under a plain <c>lock</c>, and <c>await</c>ing an async
/// <see cref="IProjection{TEvent}.ApplyAsync"/> inside a <c>lock</c> is illegal in C# — wiring the
/// runner into the real append path (via a decorator or a repository-level seam, both outside that
/// lock) is a deliberate follow-up task, not part of 4.1.
/// </para>
/// <para>
/// <b>Dispatch — no reflection in the hot loop after warm-up.</b> The constructor reflects each
/// injected projection's closed <see cref="IProjection{TEvent}"/> interface(s) exactly once,
/// building a per-event-type map of non-generic apply delegates. <see cref="RunAsync"/> resolves,
/// for each event's runtime CLR type, the matching delegates — including the type's base types and
/// implemented interfaces (contravariance) — through a cache that is populated at most once per
/// distinct runtime <see cref="Type"/>; every subsequent occurrence of that type is a plain cache
/// hit. A projection may implement <see cref="IProjection{TEvent}"/> for more than one event type;
/// each is dispatched independently.
/// </para>
/// <para>
/// <b>Pre-filter.</b> An event whose <see cref="StoredEvent.EventType"/> is unknown to the
/// registry, or is known but matched by no registered projection, is skipped WITHOUT deserializing
/// its payload — the registry lookup and the delegate-match lookup both happen before the
/// (potentially expensive) JSON deserialization.
/// </para>
/// <para>
/// <b>Idempotency (ADR-005) — bounded, per-projection high-water watermark.</b> Instead of an
/// unbounded per-event set, this runner tracks a single monotonic <see cref="GlobalPosition"/>
/// watermark per projection instance (O(#projections) memory, not O(#events)). An event is applied
/// to a given projection only if its <see cref="StoredEvent.GlobalPosition"/> is strictly greater
/// than that projection's current watermark; the watermark advances only AFTER
/// <see cref="IProjection{TEvent}.ApplyAsync"/> completes successfully (mark-after-apply), so a
/// throwing <c>ApplyAsync</c> never causes its event to be silently skipped on a later retry. The
/// watermark dictionary is guarded by a lightweight <see cref="Lock"/>; the check-and-raise steps
/// never wrap the <c>await</c> itself (C# forbids <c>await</c> inside a <c>lock</c>).
/// </para>
/// <para>
/// <b>Exception propagation.</b> An exception thrown by <see cref="IProjection{TEvent}.ApplyAsync"/>
/// propagates out of <see cref="RunAsync"/> unchanged — this runner neither swallows nor wraps it;
/// deciding what to do with a persistently failing projection (skip, dead-letter, pause) is an
/// error-handling policy that belongs to the async daemon (Feature 5), not to this inline runner.
/// No exception raised by this type embeds the event payload, the deserialized event instance, or
/// <see cref="StoredEvent.Metadata"/> (ADR-008/ADR-017 — no PII/payload echo into diagnostics).
/// </para>
/// <para>
/// Multi-pod behavior class: single-process (ADR-014) — the watermark dictionary and the delegate
/// caches are process-local, in-memory state; neither is shared or coordinated across pods.
/// </para>
/// </summary>
public sealed class InlineProjectionRunner
{
    /// <summary>A single (projection instance, apply-delegate) pair matched against a runtime event type.</summary>
    private readonly record struct DispatchEntry(object Projection, Func<object, StoredEvent, CancellationToken, ValueTask> Apply);

    private static readonly MethodInfo CreateTypedApplyDelegateMethod = typeof(InlineProjectionRunner)
        .GetMethod(nameof(CreateTypedApplyDelegate), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly EventSerializer _serializer;
    private readonly EventTypeRegistry _registry;
    private readonly Dictionary<Type, List<DispatchEntry>> _byEventType;
    private readonly ConcurrentDictionary<Type, DispatchEntry[]> _matchCache = new();
    private readonly Dictionary<object, GlobalPosition> _watermarks = [];
    private readonly Lock _watermarkLock = new();
    private readonly ILogger<InlineProjectionRunner>? _logger;

    /// <summary>
    /// Creates a runner bound to a serializer, a registry, and the set of projections to dispatch
    /// to. Reflects each projection's closed <see cref="IProjection{TEvent}"/> interface(s) exactly
    /// once here, building the per-event-type dispatch map so <see cref="RunAsync"/> never reflects.
    /// </summary>
    /// <param name="serializer">Deserializes a matched <see cref="StoredEvent"/>'s payload into a CLR instance.</param>
    /// <param name="registry">
    /// Resolves a <see cref="StoredEvent.EventType"/> name to its registered CLR type for the
    /// pre-filter — the same registry <paramref name="serializer"/> is itself bound to.
    /// </param>
    /// <param name="projections">
    /// The registered projection instances (each implementing one or more closed
    /// <see cref="IProjection{TEvent}"/>), typically supplied by DI as an <c>IEnumerable&lt;object&gt;</c>
    /// (see <c>AddInlineProjection</c>).
    /// </param>
    /// <param name="logger">
    /// Emits a structured, payload-free log entry each time a projection applies an event (task 8.6,
    /// decision D-4); <see langword="null"/> (the default) disables logging — additive, null-safe.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="serializer"/>, <paramref name="registry"/>, or <paramref name="projections"/> is <see langword="null"/>.
    /// </exception>
    public InlineProjectionRunner(
        EventSerializer serializer,
        EventTypeRegistry registry,
        IEnumerable<object> projections,
        ILogger<InlineProjectionRunner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(projections);

        _serializer = serializer;
        _registry = registry;
        _byEventType = BuildDispatchMap(projections);
        _logger = logger;
    }

    /// <summary>
    /// Dispatches each of <paramref name="appended"/> — in ascending <see cref="StoredEvent.GlobalPosition"/>
    /// order — to every registered projection whose <see cref="IProjection{TEvent}"/> matches the
    /// event's runtime CLR type (its base types and implemented interfaces included).
    /// <para>
    /// An event whose <see cref="StoredEvent.EventType"/> is unknown to the registry, or is known
    /// but matched by no projection, is skipped WITHOUT deserializing its payload.
    /// </para>
    /// <para>
    /// Idempotency: an event reaches a given projection's <see cref="IProjection{TEvent}.ApplyAsync"/>
    /// only if its <see cref="StoredEvent.GlobalPosition"/> is strictly greater than that
    /// projection's current high-water watermark; the watermark advances only after
    /// <c>ApplyAsync</c> completes successfully (mark-after-apply — ADR-005).
    /// </para>
    /// <para>
    /// An exception thrown by <c>ApplyAsync</c> propagates out of this method unchanged; no
    /// exception raised here embeds the event payload, the deserialized event, or
    /// <see cref="StoredEvent.Metadata"/> (ADR-008/ADR-017).
    /// </para>
    /// </summary>
    /// <param name="appended">The batch of stored events to dispatch.</param>
    /// <param name="ct">A token to observe for cancellation, checked before each event and forwarded to <c>ApplyAsync</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="appended"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is already cancelled, or becomes cancelled while dispatching.</exception>
    public async ValueTask RunAsync(IReadOnlyList<StoredEvent> appended, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(appended);
        ct.ThrowIfCancellationRequested();

        if (appended.Count == 0)
        {
            return;
        }

        // Defensive: the store already yields ascending GlobalPosition order, but this method's
        // own ordering and idempotency guarantees must hold regardless of caller discipline.
        IReadOnlyList<StoredEvent> ordered = [.. appended.OrderBy(static e => e.GlobalPosition)];

        foreach (var stored in ordered)
        {
            ct.ThrowIfCancellationRequested();

            if (!_registry.TryResolveClrType(stored.EventType, out var clrType))
            {
                // Unknown EventType — filtered out before any deserialization.
                continue;
            }

            var matches = ResolveMatches(clrType);
            if (matches.Length == 0)
            {
                // Registered type, but no subscribed projection — no-op, no deserialization.
                continue;
            }

            var sourced = _serializer.ToSourcedEvent(stored);

            foreach (var entry in matches)
            {
                if (!ShouldApply(entry.Projection, stored.GlobalPosition))
                {
                    continue;
                }

                var projectionName = entry.Projection.GetType().Name;

                using var activity = ActaDiagnostics.ActivitySource.StartActivity(ActaDiagnostics.ProjectionApplySpan, ActivityKind.Internal);
                activity?.SetTag(ActaDiagnostics.ProjectionNameTag, projectionName);

                await entry.Apply(sourced.Event, stored, ct).ConfigureAwait(false);

                MarkApplied(entry.Projection, stored.GlobalPosition);
                _logger?.ProjectionApplied(projectionName, stored.GlobalPosition.Value);
            }
        }
    }

    private bool ShouldApply(object projection, GlobalPosition position)
    {
        lock (_watermarkLock)
        {
            return position > _watermarks.GetValueOrDefault(projection, GlobalPosition.Start);
        }
    }

    private void MarkApplied(object projection, GlobalPosition position)
    {
        lock (_watermarkLock)
        {
            _watermarks[projection] = position;
        }
    }

    private DispatchEntry[] ResolveMatches(Type clrType) => _matchCache.GetOrAdd(clrType, BuildMatches);

    private DispatchEntry[] BuildMatches(Type clrType)
    {
        List<DispatchEntry>? matches = null;
        foreach (var candidate in TypeHierarchy(clrType))
        {
            if (_byEventType.TryGetValue(candidate, out var subscribers))
            {
                matches ??= [];
                matches.AddRange(subscribers);
            }
        }

        return matches?.ToArray() ?? [];
    }

    /// <summary>
    /// Yields <paramref name="clrType"/>, every base type up to (and including) <see cref="object"/>,
    /// then every implemented interface — the full set of keys under which a registered
    /// <see cref="IProjection{TEvent}"/> could match this runtime type (contravariance).
    /// </summary>
    private static IEnumerable<Type> TypeHierarchy(Type clrType)
    {
        for (var current = clrType; current is not null; current = current.BaseType)
        {
            yield return current;
        }

        foreach (var iface in clrType.GetInterfaces())
        {
            yield return iface;
        }
    }

    private static Dictionary<Type, List<DispatchEntry>> BuildDispatchMap(IEnumerable<object> projections)
    {
        var map = new Dictionary<Type, List<DispatchEntry>>();

        foreach (var projection in projections)
        {
            foreach (var iface in projection.GetType().GetInterfaces())
            {
                if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IProjection<>))
                {
                    continue;
                }

                var eventType = iface.GetGenericArguments()[0];
                var apply = (Func<object, StoredEvent, CancellationToken, ValueTask>)CreateTypedApplyDelegateMethod
                    .MakeGenericMethod(eventType)
                    .Invoke(null, [projection])!;

                if (!map.TryGetValue(eventType, out var list))
                {
                    list = [];
                    map[eventType] = list;
                }

                list.Add(new DispatchEntry(projection, apply));
            }
        }

        return map;
    }

    /// <summary>
    /// Built once per (projection, <typeparamref name="TEvent"/>) pair via
    /// <see cref="MethodInfo.MakeGenericMethod"/> at construction time — the only reflection this
    /// type performs. The returned delegate itself is a plain, fully-typed closure with no further
    /// reflection on every dispatch.
    /// </summary>
    private static Func<object, StoredEvent, CancellationToken, ValueTask> CreateTypedApplyDelegate<TEvent>(object projection)
    {
        var typed = (IProjection<TEvent>)projection;
        return (@event, raw, ct) => typed.ApplyAsync((TEvent)@event, raw, ct);
    }
}
