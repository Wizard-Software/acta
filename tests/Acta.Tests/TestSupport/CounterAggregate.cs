using Acta.Abstractions;

namespace Acta.Tests.TestSupport;

/// <summary>
/// A total-<c>Apply</c> test aggregate (task 3.1 kit): folds <see cref="Incremented"/> /
/// <see cref="Decremented"/> into a running counter and treats every other event type as a
/// no-op — the shape the AK-4 totality property test exercises through
/// <see cref="AggregateRoot.LoadFromHistory"/>.
/// </summary>
public sealed class CounterAggregate : AggregateRoot
{
    /// <summary>Number of <see cref="Incremented"/>/<see cref="Decremented"/> events folded so far.</summary>
    public int Applied { get; private set; }

    /// <summary>Number of unrecognized event types folded so far (the totality no-op branch).</summary>
    public int Ignored { get; private set; }

    /// <summary>Command: records a new <see cref="Incremented"/> event.</summary>
    public void Increment() => Raise(new Incremented());

    /// <summary>Command: records a new <see cref="Decremented"/> event.</summary>
    public void Decrement() => Raise(new Decremented());

    /// <summary>
    /// Test-only escape hatch for the frozen contract's <c>protected set</c> <see cref="AggregateRoot.Id"/> —
    /// lets tests assign identity without adding a public setter to the boundary contract.
    /// </summary>
    public void AssignId(string id) => Id = id;

    /// <summary>Test-only escape hatch: calls the protected <c>Raise</c> with a <see langword="null"/>
    /// event, so tests can assert the null guard without reflection.</summary>
    public void RaiseNull() => Raise(null!);

    /// <summary>Total mutator (FR-11, AK-4): known event types update the counters; any other
    /// type falls through to the <c>default</c> no-op branch — it never throws.</summary>
    protected override void Apply(object @event)
    {
        switch (@event)
        {
            case Incremented:
                Applied++;
                break;
            case Decremented:
                Applied--;
                break;
            default:
                Ignored++;
                break;
        }
    }
}

/// <summary>A known event recognized by <see cref="CounterAggregate.Apply"/> — increments the counter.</summary>
public sealed record Incremented();

/// <summary>A known event recognized by <see cref="CounterAggregate.Apply"/> — decrements the counter.</summary>
public sealed record Decremented();

/// <summary>
/// An event type <see cref="CounterAggregate.Apply"/> does not recognize — exercises the totality
/// no-op (<c>default</c>) branch. Models both "unknown event type" and "event newer than the
/// aggregate knows" (§6.1): both surface as a CLR type <c>Apply</c> does not switch on.
/// </summary>
public sealed record UnknownEvent(int Payload);
