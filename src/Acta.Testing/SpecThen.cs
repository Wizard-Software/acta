using Acta.Abstractions;

namespace Acta.Testing;

/// <summary>
/// Terminal stage of a <see cref="Spec"/>: builds the aggregate, rehydrates it from the events
/// supplied to <see cref="SpecFor{T}.Given"/> (if any), runs the command captured by
/// <c>When</c>, and asserts either the events it produced (<see cref="Then"/>) or the exception
/// it raised (<see cref="ThenThrows{TException}"/>).
/// </summary>
/// <typeparam name="T">The aggregate root under test.</typeparam>
/// <example>
/// <code>
/// await Spec.For&lt;Order&gt;()
///     .Given(new OrderPlaced("o-1", "c-1"), new OrderCancelled("o-1", "x"))
///     .When(o => o.Cancel("again"))
///     .ThenThrows&lt;InvalidOperationException&gt;();
/// </code>
/// </example>
public sealed class SpecThen<T> where T : AggregateRoot, new()
{
    private readonly object[] _history;
    private readonly Action<T> _action;

    internal SpecThen(object[] history, Action<T> action)
    {
        _history = history;
        _action = action;
    }

    /// <summary>
    /// Runs the command and asserts it produced exactly <paramref name="expectedEvents"/>, in
    /// order, compared structurally (<see cref="object.Equals(object?, object?)"/> — records
    /// compare by value, per CONSTITUTION §1.3).
    /// </summary>
    /// <param name="expectedEvents">The events the command is expected to have raised, in order.</param>
    /// <returns>
    /// A completed <see cref="Task"/> on success. The assertion runs synchronously under the
    /// hood (fake-async facade): a failure is detected — and thrown — even if the caller forgets
    /// to <see langword="await"/> the result.
    /// </returns>
    /// <exception cref="SpecAssertionException">
    /// The command raised an unexpected exception (available as <see cref="Exception.InnerException"/>),
    /// the number of produced events does not match <paramref name="expectedEvents"/>, or the
    /// event at some index does not structurally equal the expected one.
    /// </exception>
    public Task Then(params object[] expectedEvents)
    {
        var (produced, thrown) = Execute();

        if (thrown is not null)
        {
            throw new SpecAssertionException(
                $"Expected {expectedEvents.Length} event(s) but the command raised {thrown.GetType()}: {thrown.Message}",
                thrown);
        }

        if (produced.Count != expectedEvents.Length)
        {
            throw new SpecAssertionException(
                $"Expected {expectedEvents.Length} event(s) but the command produced {produced.Count}.");
        }

        for (var i = 0; i < produced.Count; i++)
        {
            if (!Equals(produced[i], expectedEvents[i]))
            {
                throw new SpecAssertionException(
                    $"Event mismatch at index {i}: expected '{expectedEvents[i]}' but was '{produced[i]}'.");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs the command and asserts it raised an exception assignable to
    /// <typeparamref name="TException"/> — exact type or subtype, matched via
    /// <see langword="is"/> (the natural .NET semantics).
    /// </summary>
    /// <typeparam name="TException">The expected exception type, or a base type of it.</typeparam>
    /// <returns>
    /// A completed <see cref="Task"/> on success. The assertion runs synchronously under the
    /// hood (fake-async facade): a failure is detected — and thrown — even if the caller forgets
    /// to <see langword="await"/> the result.
    /// </returns>
    /// <exception cref="SpecAssertionException">
    /// The command did not raise an exception, or it raised one that is not assignable to
    /// <typeparamref name="TException"/> (the actual exception is available as
    /// <see cref="Exception.InnerException"/>).
    /// </exception>
    public Task ThenThrows<TException>() where TException : Exception
    {
        var (produced, thrown) = Execute();

        if (thrown is null)
        {
            throw new SpecAssertionException(
                $"Expected an exception of type '{typeof(TException)}' but the command completed " +
                $"successfully and produced {produced.Count} event(s).");
        }

        if (thrown is not TException)
        {
            throw new SpecAssertionException(
                $"Expected an exception of type '{typeof(TException)}' but the command raised '{thrown.GetType()}'.",
                thrown);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the aggregate, rehydrates it from the history captured by <see cref="SpecFor{T}.Given"/>,
    /// clears the uncommitted queue (defensive — <see cref="AggregateRoot.LoadFromHistory"/> never
    /// queues), then runs the captured command, catching any exception it raises.
    /// </summary>
    private (IReadOnlyList<object> Produced, Exception? Thrown) Execute()
    {
        var aggregate = new T();
        aggregate.LoadFromHistory(_history);
        aggregate.ClearUncommittedEvents();

        try
        {
            _action(aggregate);
            return (aggregate.UncommittedEvents, null);
        }
        catch (Exception ex)
        {
            return (aggregate.UncommittedEvents, ex);
        }
    }
}
