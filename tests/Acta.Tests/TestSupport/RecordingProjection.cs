using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// A hand-rolled, recording <see cref="IProjection{TEvent}"/> for the async daemon tests (task 5.2):
/// it collects every applied event with its underlying <see cref="StoredEvent"/> and exposes an
/// optional <see cref="OnApply"/> hook so a test can make it throw (baseline error policy) or signal
/// (graceful-stop synchronization). No mocking library (plan §4).
/// </summary>
/// <typeparam name="TEvent">The event type this projection consumes.</typeparam>
public sealed class RecordingProjection<TEvent> : IProjection<TEvent>
{
    /// <summary>Every (event, raw) pair applied so far, in application order.</summary>
    public List<(TEvent Event, StoredEvent Raw)> Applied { get; } = [];

    /// <summary>Optional hook invoked before recording — lets a test throw or signal on apply.</summary>
    public Func<TEvent, StoredEvent, ValueTask>? OnApply { get; init; }

    /// <inheritdoc/>
    public async ValueTask ApplyAsync(TEvent @event, StoredEvent raw, CancellationToken ct = default)
    {
        if (OnApply is not null)
        {
            await OnApply(@event, raw);
        }

        Applied.Add((@event, raw));
    }
}
