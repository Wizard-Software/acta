using Microsoft.Extensions.Logging;

namespace Acta.Tests.TestSupport;

/// <summary>
/// Minimal <see cref="ILoggerFactory"/> test double bound to a single, already-built
/// <see cref="ILoggerProvider"/> — every <see cref="CreateLogger"/> call is forwarded straight to
/// that provider, regardless of category name. Paired with the open-generic
/// <c>services.AddSingleton(typeof(ILogger&lt;&gt;), typeof(Logger&lt;&gt;))</c> registration (the
/// <c>Logger&lt;T&gt;</c> wrapper ships in <c>Microsoft.Extensions.Logging.Abstractions</c>), this
/// lets DI resolve an <c>ILogger&lt;T&gt;</c> for an internal type (like
/// <c>Acta.Configuration.ActaOptionsValidator</c>, invisible to this test project) that writes
/// into a <see cref="ListLoggerProvider"/> — without adding the concrete
/// <c>Microsoft.Extensions.Logging</c> package as a test dependency.
/// </summary>
/// <param name="provider">The single provider every created logger writes through.</param>
public sealed class SingleProviderLoggerFactory(ILoggerProvider provider) : ILoggerFactory
{
    /// <inheritdoc/>
    /// <remarks>No-op: this test double is deliberately bound to one provider only.</remarks>
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

    /// <inheritdoc/>
    public void Dispose()
    {
        // No unmanaged resources or event subscriptions to release — a forwarding test double only.
    }
}
