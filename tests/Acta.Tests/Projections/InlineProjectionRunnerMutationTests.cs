using System.Text.Json;

using Xunit;

using Microsoft.Extensions.Logging;

using Acta.Abstractions;
using Acta.Projections.Inline;
using Acta.Serialization;
using Acta.Tests.TestSupport;

namespace Acta.Tests.Projections;

/// <summary>
/// Additional mutation-kill coverage for <see cref="InlineProjectionRunner"/> (task 4.1) beyond
/// <c>InlineProjectionRunnerTests</c>: constructor/method argument guards, the upfront-vs-loop-body
/// cancellation pair, the structured "applied" log line, the <c>matches ??= []</c> accumulator
/// (multi-level type-hierarchy matches), interface-only dispatch (<c>TypeHierarchy</c>'s interface
/// leg), and the constructor's own generic-interface guard clause.
/// </summary>
public sealed class InlineProjectionRunnerMutationTests
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Default;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record TestEventA(string Value);

    private record BaseEvent(string Id);

    private sealed record DerivedEvent(string Id, string Detail) : BaseEvent(Id);

    private interface ITaggedEvent
    {
        string Id { get; }
    }

    private sealed record TaggedEvent(string Id) : ITaggedEvent;

    /// <summary>A hand-rolled, recording fake <see cref="IProjection{TEvent}"/> — no mocking library (plan §4).</summary>
    private sealed class RecordingProjection<T> : IProjection<T>
    {
        public List<(T Event, StoredEvent Raw)> Calls { get; } = [];

        public Func<T, StoredEvent, ValueTask>? OnApply { get; init; }

        public ValueTask ApplyAsync(T @event, StoredEvent raw, CancellationToken ct = default)
        {
            Calls.Add((@event, raw));
            return OnApply?.Invoke(@event, raw) ?? ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A projection implementing <see cref="IProjection{TEvent}"/> AND an unrelated closed generic
    /// interface — pins the constructor's guard clause (only <c>IProjection&lt;&gt;</c> interfaces are
    /// dispatch candidates; every other generic interface must be skipped before it reaches
    /// <c>MakeGenericMethod</c>/cast machinery, which would otherwise throw for a non-matching type).
    /// </summary>
    private sealed class ProjectionWithExtraInterface : IProjection<TestEventA>, IComparable<string>
    {
        public List<TestEventA> Applied { get; } = [];

        public ValueTask ApplyAsync(TestEventA @event, StoredEvent raw, CancellationToken ct = default)
        {
            Applied.Add(@event);
            return ValueTask.CompletedTask;
        }

        public int CompareTo(string? other) => 0;
    }

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    private static StoredEvent BuildStoredEvent(EventSerializer serializer, object @event, long globalPosition, string streamId = "stream-1")
    {
        var eventData = serializer.ToEventData(@event, CreateMetadata(), Guid.NewGuid());
        return new StoredEvent(
            eventData.EventId,
            streamId,
            0,
            new GlobalPosition(globalPosition),
            eventData.EventType,
            eventData.SchemaVersion,
            eventData.Payload,
            eventData.Metadata,
            DateTimeOffset.UtcNow);
    }

    // ── Constructor guards (114-116) ──────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSerializer_Throws()
    {
        var registry = new EventTypeRegistry();
        Invoking(() => new InlineProjectionRunner(null!, registry, []))
            .Should().Throw<ArgumentNullException>().WithParameterName("serializer");
    }

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        var registry = new EventTypeRegistry();
        var serializer = new EventSerializer(registry, Options);
        Invoking(() => new InlineProjectionRunner(serializer, null!, []))
            .Should().Throw<ArgumentNullException>().WithParameterName("registry");
    }

    [Fact]
    public void Constructor_NullProjections_Throws()
    {
        var registry = new EventTypeRegistry();
        var serializer = new EventSerializer(registry, Options);
        Invoking(() => new InlineProjectionRunner(serializer, registry, null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("projections");
    }

    // ── RunAsync argument / cancellation guards (150, 151, 164) ───────────────────────────────────

    [Fact]
    public async Task RunAsync_NullAppended_Throws()
    {
        var registry = new EventTypeRegistry();
        var serializer = new EventSerializer(registry, Options);
        var runner = new InlineProjectionRunner(serializer, registry, []);

        await Awaiting(() => runner.RunAsync(null!, Ct).AsTask())
            .Should().ThrowAsync<ArgumentNullException>().WithParameterName("appended");
    }

    [Fact]
    public async Task RunAsync_EmptyBatchWithAlreadyCancelledToken_ThrowsBeforeTheEmptyCountShortCircuit()
    {
        // An empty batch never enters the per-event loop, so this is the ONLY way to isolate the
        // upfront ct.ThrowIfCancellationRequested() from the loop-body one below: without the
        // upfront check, an empty batch would just return normally (a no-op), never observing
        // cancellation at all.
        var registry = new EventTypeRegistry();
        var serializer = new EventSerializer(registry, Options);
        var runner = new InlineProjectionRunner(serializer, registry, []);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => runner.RunAsync([], cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_CancelledDuringEnumeration_StopsBeforeTheSecondEvent()
    {
        // The token is NOT cancelled at call time (so the upfront check passes) — it is cancelled
        // from inside the first event's ApplyAsync, so only the per-iteration check (inside the
        // foreach) can observe it before the second event would otherwise be dispatched.
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        using var cts = new CancellationTokenSource();
        var projection = new RecordingProjection<TestEventA>
        {
            OnApply = (_, _) =>
            {
                cts.Cancel();
                return ValueTask.CompletedTask;
            },
        };
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var first = BuildStoredEvent(serializer, new TestEventA("first"), 1);
        var second = BuildStoredEvent(serializer, new TestEventA("second"), 2);

        await Awaiting(() => runner.RunAsync([first, second], cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();

        projection.Calls.Should().ContainSingle(); // the second event was never reached
    }

    // ── Structured "applied" log line (196) ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MatchingProjection_LogsAppliedEventAtPosition()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new RecordingProjection<TestEventA>();
        var logProvider = new ListLoggerProvider();
        var logger = new Logger<InlineProjectionRunner>(new SingleProviderLoggerFactory(logProvider));
        var runner = new InlineProjectionRunner(serializer, registry, [projection], logger);
        var stored = BuildStoredEvent(serializer, new TestEventA("value"), 7);

        await runner.RunAsync([stored], Ct);

        logProvider.Entries.Should().Contain(e =>
            e.Level == LogLevel.Debug && e.Message.Contains("applied event at position 7", StringComparison.Ordinal));
    }

    // ── matches ??= [] accumulator across multiple type-hierarchy levels (226) ───────────────────

    [Fact]
    public async Task RunAsync_ProjectionsAtTwoHierarchyLevels_BothInvokedNotOnlyTheLast()
    {
        // DerivedEvent matches at TWO distinct TypeHierarchy candidates: the concrete type itself
        // (DerivedEvent) and its base type (BaseEvent). BuildMatches' accumulator MUST keep both
        // sets of subscribers — if `matches ??= []` regresses to `matches = []`, the second
        // candidate's AddRange wipes out the first candidate's matches entirely.
        var registry = new EventTypeRegistry().Register<DerivedEvent>();
        var serializer = new EventSerializer(registry, Options);
        var derivedProjection = new RecordingProjection<DerivedEvent>();
        var baseProjection = new RecordingProjection<BaseEvent>();
        var runner = new InlineProjectionRunner(serializer, registry, [derivedProjection, baseProjection]);
        var stored = BuildStoredEvent(serializer, new DerivedEvent("id-1", "detail"), 1);

        await runner.RunAsync([stored], Ct);

        derivedProjection.Calls.Should().ContainSingle();
        baseProjection.Calls.Should().ContainSingle();
    }

    // ── TypeHierarchy's interface leg (248) ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ProjectionTypedToImplementedInterface_ReceivesTheEvent()
    {
        // TaggedEvent matches ITaggedEvent ONLY through TypeHierarchy's interface leg — never
        // through the base-type chain (its only base type is object). If that leg is dropped, this
        // projection is never matched and never applied.
        var registry = new EventTypeRegistry().Register<TaggedEvent>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new RecordingProjection<ITaggedEvent>();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var stored = BuildStoredEvent(serializer, new TaggedEvent("id-1"), 1);

        await runner.RunAsync([stored], Ct);

        var call = projection.Calls.Should().ContainSingle().Subject;
        call.Event.Id.Should().Be("id-1");
    }

    // ── Constructor's IProjection<> guard clause (260, 262) ───────────────────────────────────────

    [Fact]
    public void Constructor_ProjectionImplementsUnrelatedClosedGenericInterface_IgnoresItWithoutThrowing()
    {
        // ProjectionWithExtraInterface also implements IComparable<string>. The constructor's guard
        // (`!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IProjection<>)`) must
        // skip that interface via `continue`. If the `||` regresses to `&&`, or the `continue`
        // regresses to a no-op, IComparable<string> is treated as an IProjection<string> candidate:
        // CreateTypedApplyDelegate<string> casts the projection to IProjection<string>, which it does
        // NOT implement, throwing InvalidCastException (wrapped) right here at construction.
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new ProjectionWithExtraInterface();

        Invoking(() => new InlineProjectionRunner(serializer, registry, [projection]))
            .Should().NotThrow();
    }

    [Fact]
    public async Task RunAsync_ProjectionWithUnrelatedInterface_StillAppliesItsOwnEventType()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new ProjectionWithExtraInterface();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var stored = BuildStoredEvent(serializer, new TestEventA("value"), 1);

        await runner.RunAsync([stored], Ct);

        projection.Applied.Should().ContainSingle().Which.Value.Should().Be("value");
    }
}
