using Acta.Abstractions;

namespace Acta.Testing;

/// <summary>
/// Second stage of a <see cref="Spec"/>, reached after <see cref="SpecFor{T}.Given"/>: captures
/// the command under test.
/// </summary>
/// <typeparam name="T">The aggregate root under test.</typeparam>
public sealed class SpecWhen<T> where T : AggregateRoot, new()
{
    private readonly object[] _history;

    internal SpecWhen(object[] history) => _history = history;

    /// <summary>
    /// Captures the command under test.
    /// </summary>
    /// <remarks>
    /// The delegate is NOT executed here — execution is deferred to the terminal stage
    /// (<see cref="SpecThen{T}.Then"/> / <see cref="SpecThen{T}.ThenThrows{TException}"/>) so
    /// that an exception raised by the command cannot escape before the assertion runs.
    /// </remarks>
    /// <param name="action">The command to execute against the rehydrated aggregate.</param>
    /// <returns>A builder for the terminal ("Then" / "ThenThrows") stage.</returns>
    public SpecThen<T> When(Action<T> action) => new(_history, action);
}
