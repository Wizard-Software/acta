namespace Acta.Testing;

/// <summary>
/// Thrown by the <see cref="Spec"/> Given-When-Then harness when a terminal assertion —
/// <see cref="SpecThen{T}.Then"/> or <see cref="SpecThen{T}.ThenThrows{TException}"/> — fails.
/// <para>
/// This type is deliberately framework-agnostic: <c>Acta.Testing</c> does not depend on any test
/// runner or assertion library (xUnit, NUnit, AwesomeAssertions, ...), so every test framework
/// reports an unhandled <see cref="SpecAssertionException"/> as a failed test without needing an
/// adapter.
/// </para>
/// </summary>
public sealed class SpecAssertionException : Exception
{
    /// <summary>Creates a new assertion failure with a human-readable diagnostic message.</summary>
    /// <param name="message">
    /// Describes what failed — e.g. the expected vs. actual event count, the first differing
    /// event index, or the expected vs. actual exception type.
    /// </param>
    public SpecAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new assertion failure that wraps another exception as <see cref="Exception.InnerException"/> —
    /// either the exception the command under test raised when <see cref="SpecThen{T}.Then"/> did
    /// not expect one, or the exception it actually raised when
    /// <see cref="SpecThen{T}.ThenThrows{TException}"/> expected a different type.
    /// </summary>
    /// <param name="message">Describes what failed.</param>
    /// <param name="inner">The exception that caused, or explains, this assertion failure.</param>
    public SpecAssertionException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
