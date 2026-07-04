using Acta.Abstractions;

namespace Acta.Testing;

/// <summary>
/// Entry point for the Given-When-Then specification harness (FR-15): builds and exercises an
/// <see cref="AggregateRoot"/> directly, in-memory — no event store, no adapter.
/// </summary>
/// <example>
/// <code>
/// await Spec.For&lt;Order&gt;()
///     .Given(new OrderPlaced("o-1", "c-1"))
///     .When(o => o.Cancel("duplicate"))
///     .Then(new OrderCancelled("o-1", "duplicate"));
///
/// await Spec.For&lt;Order&gt;()
///     .Given(new OrderPlaced("o-1", "c-1"), new OrderCancelled("o-1", "x"))
///     .When(o => o.Cancel("again"))
///     .ThenThrows&lt;InvalidOperationException&gt;();
/// </code>
/// </example>
public static class Spec
{
    /// <summary>Starts a new specification for aggregate type <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">
    /// The aggregate root under test. Must derive from <see cref="AggregateRoot"/> and expose a
    /// public parameterless constructor.
    /// </typeparam>
    /// <returns>A builder for the "Given" (history) or "When" (creating command) stage.</returns>
    public static SpecFor<T> For<T>() where T : AggregateRoot, new() => new();
}
