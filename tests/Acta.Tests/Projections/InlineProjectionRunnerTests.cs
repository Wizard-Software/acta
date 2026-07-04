using System.Text.Json;

using Xunit;

using Acta.Abstractions;
using Acta.Projections.Inline;
using Acta.Serialization;

namespace Acta.Tests.Projections;

/// <summary>
/// Unit tests for <see cref="InlineProjectionRunner"/> (task 4.1): dispatch by runtime event type,
/// the pre-filter that avoids deserializing unsubscribed events, per-projection high-water
/// idempotency, multiple projections per event, contravariance, ordering, cancellation, and
/// exception propagation.
/// </summary>
public sealed class InlineProjectionRunnerTests
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Default;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record TestEventA(string Value);

    private sealed record UnwatchedEvent(string Value);

    private record BaseEvent(string Id);

    private sealed record DerivedEvent(string Id, string Detail) : BaseEvent(Id);

    /// <summary>A hand-rolled, recording fake <see cref="IProjection{TEvent}"/> — no NSubstitute (plan §4).</summary>
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

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    /// <summary>Builds a <see cref="StoredEvent"/> by round-tripping <paramref name="event"/> through <paramref name="serializer"/>, mirroring <c>EventSerializerTests</c>' construction pattern.</summary>
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

    [Fact]
    public async Task RunAsync_MatchingProjection_CallsApplyAsyncOnceWithCorrectRaw()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new RecordingProjection<TestEventA>();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var stored = BuildStoredEvent(serializer, new TestEventA("hello"), 1);

        await runner.RunAsync([stored], Ct);

        var call = projection.Calls.Should().ContainSingle().Subject;
        call.Raw.Should().BeSameAs(stored);
        call.Event.Value.Should().Be("hello");
    }

    [Fact]
    public async Task RunAsync_RegisteredTypeWithoutMatchingProjection_SkipsWithoutDeserializingPayload()
    {
        var registry = new EventTypeRegistry().Register<UnwatchedEvent>();
        var serializer = new EventSerializer(registry, Options);
        var runner = new InlineProjectionRunner(serializer, registry, []);

        // The payload is deliberately invalid JSON: if this runner attempted to deserialize it
        // (it must not, since no projection subscribes to UnwatchedEvent), RunAsync would throw.
        var stored = new StoredEvent(
            Guid.NewGuid(),
            "stream-1",
            0,
            new GlobalPosition(1),
            nameof(UnwatchedEvent),
            1,
            "{ not valid json"u8.ToArray(),
            CreateMetadata(),
            DateTimeOffset.UtcNow);

        await Awaiting(() => runner.RunAsync([stored], Ct).AsTask()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_UnknownEventType_SkipsWithoutThrowing()
    {
        var registry = new EventTypeRegistry();
        var serializer = new EventSerializer(registry, Options);
        var runner = new InlineProjectionRunner(serializer, registry, []);
        var stored = new StoredEvent(
            Guid.NewGuid(),
            "stream-1",
            0,
            new GlobalPosition(1),
            "Ghost",
            1,
            "{ not valid json"u8.ToArray(),
            CreateMetadata(),
            DateTimeOffset.UtcNow);

        await Awaiting(() => runner.RunAsync([stored], Ct).AsTask()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_MatchingProjection_DeserializesCorrectFieldValues()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new RecordingProjection<TestEventA>();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var stored = BuildStoredEvent(serializer, new TestEventA("precise-value"), 1);

        await runner.RunAsync([stored], Ct);

        var call = projection.Calls.Should().ContainSingle().Subject;
        call.Event.Should().Be(new TestEventA("precise-value"));
        call.Raw.Should().BeSameAs(stored);
    }

    [Fact]
    public async Task RunAsync_SameBatchRunTwice_AppliesEachEventOnlyOnce()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new RecordingProjection<TestEventA>();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var stored = BuildStoredEvent(serializer, new TestEventA("value"), 1);

        await runner.RunAsync([stored], Ct);
        await runner.RunAsync([stored], Ct);

        projection.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_EventAtOrBelowWatermark_IsSkippedEvenInADifferentBatch()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new RecordingProjection<TestEventA>();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var first = BuildStoredEvent(serializer, new TestEventA("first"), 1);
        var second = BuildStoredEvent(serializer, new TestEventA("second"), 2);

        await runner.RunAsync([first, second], Ct);
        // Redeliver "first" alone: its GlobalPosition (1) is <= the projection's watermark (2).
        await runner.RunAsync([first], Ct);

        projection.Calls.Select(c => c.Event.Value).Should().Equal("first", "second");
    }

    [Fact]
    public async Task RunAsync_MultipleProjectionsForSameEventType_AllInvokedInRegistrationOrder()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var order = new List<string>();
        var first = new RecordingProjection<TestEventA>
        {
            OnApply = (_, _) =>
            {
                order.Add("first");
                return ValueTask.CompletedTask;
            },
        };
        var second = new RecordingProjection<TestEventA>
        {
            OnApply = (_, _) =>
            {
                order.Add("second");
                return ValueTask.CompletedTask;
            },
        };
        var runner = new InlineProjectionRunner(serializer, registry, [first, second]);
        var stored = BuildStoredEvent(serializer, new TestEventA("value"), 1);

        await runner.RunAsync([stored], Ct);

        first.Calls.Should().ContainSingle();
        second.Calls.Should().ContainSingle();
        order.Should().Equal("first", "second");
    }

    [Fact]
    public async Task RunAsync_ProjectionTypedToBaseEvent_ReceivesDerivedEventInstance()
    {
        var registry = new EventTypeRegistry().Register<DerivedEvent>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new RecordingProjection<BaseEvent>();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var stored = BuildStoredEvent(serializer, new DerivedEvent("id-1", "detail"), 1);

        await runner.RunAsync([stored], Ct);

        var call = projection.Calls.Should().ContainSingle().Subject;
        call.Event.Should().BeOfType<DerivedEvent>();
        call.Event.Id.Should().Be("id-1");
    }

    [Fact]
    public async Task RunAsync_EventsOutOfOrderInBatch_AppliesInAscendingGlobalPositionOrder()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new RecordingProjection<TestEventA>();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var first = BuildStoredEvent(serializer, new TestEventA("first"), 1);
        var second = BuildStoredEvent(serializer, new TestEventA("second"), 2);
        var third = BuildStoredEvent(serializer, new TestEventA("third"), 3);

        // Deliberately out of GlobalPosition order.
        await runner.RunAsync([third, first, second], Ct);

        projection.Calls.Select(c => c.Event.Value).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task RunAsync_AlreadyCancelledToken_ThrowsWithoutApplying()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var projection = new RecordingProjection<TestEventA>();
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var stored = BuildStoredEvent(serializer, new TestEventA("value"), 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => runner.RunAsync([stored], cts.Token).AsTask()).Should().ThrowAsync<OperationCanceledException>();

        projection.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_ApplyAsyncThrows_PropagatesExceptionAndDoesNotAdvanceWatermark()
    {
        var registry = new EventTypeRegistry().Register<TestEventA>();
        var serializer = new EventSerializer(registry, Options);
        var attempt = 0;
        var projection = new RecordingProjection<TestEventA>
        {
            OnApply = (_, _) =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new InvalidOperationException("boom");
                }

                return ValueTask.CompletedTask;
            },
        };
        var runner = new InlineProjectionRunner(serializer, registry, [projection]);
        var stored = BuildStoredEvent(serializer, new TestEventA("value"), 1);

        await Awaiting(() => runner.RunAsync([stored], Ct).AsTask()).Should().ThrowAsync<InvalidOperationException>();

        // The watermark was NOT advanced after the failing attempt (mark-after-apply) — a
        // subsequent, successful run re-applies the very same event instead of skipping it.
        await runner.RunAsync([stored], Ct);

        attempt.Should().Be(2);
        projection.Calls.Should().HaveCount(2);
    }
}
