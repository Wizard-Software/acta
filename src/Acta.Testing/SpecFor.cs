using Acta.Abstractions;

namespace Acta.Testing;

/// <summary>
/// First stage of a <see cref="Spec"/>: either supplies the prior history the aggregate is
/// rehydrated from (<see cref="Given"/>) or, for a creating command with no prior state,
/// captures the command directly (<see cref="When"/>).
/// </summary>
/// <typeparam name="T">The aggregate root under test.</typeparam>
public sealed class SpecFor<T> where T : AggregateRoot, new()
{
    /// <summary>
    /// Supplies the events the aggregate is rehydrated from before the command under test runs.
    /// </summary>
    /// <remarks>
    /// Rehydration is deferred to the terminal stage (<see cref="SpecThen{T}.Then"/> /
    /// <see cref="SpecThen{T}.ThenThrows{TException}"/>), which calls
    /// <see cref="AggregateRoot.LoadFromHistory"/> — so a <see langword="null"/> <paramref name="events"/>
    /// array surfaces its <see cref="ArgumentNullException"/> there, not here.
    /// </remarks>
    /// <param name="events">The events to fold into the aggregate, oldest first.</param>
    /// <returns>A builder for the "When" (command) stage.</returns>
    public SpecWhen<T> Given(params object[] events) => new(events);

    /// <summary>
    /// Captures a creating command — one that needs no prior history — without calling
    /// <see cref="Given"/> first. Equivalent to <c>Given().When(action)</c>.
    /// </summary>
    /// <param name="action">
    /// The command to execute against a freshly constructed <typeparamref name="T"/>. Not
    /// executed until the terminal stage runs.
    /// </param>
    /// <returns>A builder for the terminal ("Then" / "ThenThrows") stage.</returns>
    public SpecThen<T> When(Action<T> action) => new([], action);
}
