using Xunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Acta.Abstractions;
using Acta.Correlation;

namespace Acta.Tests.Correlation;

/// <summary>
/// Unit tests for <see cref="AsyncLocalCorrelationContextAccessor"/> (task 6.2, Grupa 6): scope
/// begin/dispose semantics (LIFO nesting, idempotent dispose), the null-context guard, and the
/// R5 risk test proving that an explicitly re-seeded correlation context wins over a foreign
/// ambient value observed across a thread boundary.
/// </summary>
public sealed class AsyncLocalCorrelationContextAccessorTests
{
    [Fact]
    public void Current_NoScope_ReturnsNull()
    {
        var accessor = new AsyncLocalCorrelationContextAccessor();

        accessor.Current.Should().BeNull();
    }

    [Fact]
    public void BeginScope_WithinScope_CurrentReturnsSeededContext()
    {
        var accessor = new AsyncLocalCorrelationContextAccessor();
        var context = new CorrelationContext { CorrelationId = Guid.CreateVersion7(), CausationId = Guid.CreateVersion7() };

        using var scope = accessor.BeginScope(context);

        accessor.Current.Should().BeSameAs(context);
    }

    [Fact]
    public void BeginScope_AfterDispose_CurrentRevertsToPrevious()
    {
        var accessor = new AsyncLocalCorrelationContextAccessor();
        var context = new CorrelationContext { CorrelationId = Guid.CreateVersion7(), CausationId = Guid.CreateVersion7() };

        var scope = accessor.BeginScope(context);
        scope.Dispose();

        accessor.Current.Should().BeNull();
    }

    [Fact]
    public void BeginScope_Nested_RestoresOuterScopeLifo()
    {
        var accessor = new AsyncLocalCorrelationContextAccessor();
        var outer = new CorrelationContext { CorrelationId = Guid.CreateVersion7(), CausationId = Guid.CreateVersion7() };
        var inner = new CorrelationContext { CorrelationId = Guid.CreateVersion7(), CausationId = Guid.CreateVersion7() };

        using var outerScope = accessor.BeginScope(outer);
        var innerScope = accessor.BeginScope(inner);
        accessor.Current.Should().BeSameAs(inner);

        innerScope.Dispose();

        accessor.Current.Should().BeSameAs(outer);
    }

    [Fact]
    public void BeginScope_NullContext_ThrowsArgumentNullException()
    {
        var accessor = new AsyncLocalCorrelationContextAccessor();

        Invoking(() => accessor.BeginScope(null!)).Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("context");
    }

    [Fact]
    public void BeginScope_DisposedTwice_IsIdempotent()
    {
        var accessor = new AsyncLocalCorrelationContextAccessor();
        var context = new CorrelationContext { CorrelationId = Guid.CreateVersion7(), CausationId = Guid.CreateVersion7() };
        var scope = accessor.BeginScope(context);

        scope.Dispose();
        Invoking(() => scope.Dispose()).Should().NotThrow();

        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Current_AcrossThreadBoundaryWithForeignAmbient_ExplicitSeedTakesPrecedence()
    {
        // Arrange: the "producer" (X) scope stamps the business transaction's correlation.
        var accessor = new AsyncLocalCorrelationContextAccessor();
        var producer = new CorrelationContext { CorrelationId = Guid.CreateVersion7(), CausationId = Guid.CreateVersion7() };
        var foreign = new CorrelationContext { CorrelationId = Guid.CreateVersion7(), CausationId = Guid.CreateVersion7() };

        ICorrelationContext captured;
        using (accessor.BeginScope(producer))
        {
            // Explicit capture at the enqueue boundary — the contract callers must follow before
            // hopping to another thread (R5).
            captured = accessor.Current!;
        }

        ICorrelationContext? foreignAmbientObserved = null;
        ICorrelationContext? reSeededObserved = null;

        // Act: hop onto a pooled thread (fire-and-forget style boundary).
        await Task.Run(() =>
        {
            // Simulate a foreign ambient context leaking onto the pooled thread — a naive read of
            // Current here proves the leak surface R5 documents.
            using var foreignScope = accessor.BeginScope(foreign);
            foreignAmbientObserved = accessor.Current;

            // Re-seeding with the explicitly captured context must win over the foreign ambient one.
            using var reSeededScope = accessor.BeginScope(captured);
            reSeededObserved = accessor.Current;
        });

        // Assert: the leak surface is real (naive read == foreign) ...
        foreignAmbientObserved.Should().BeSameAs(foreign);

        // ... but an explicit re-seed always takes precedence over it (R5 mitigation).
        reSeededObserved!.CorrelationId.Should().Be(producer.CorrelationId);
        reSeededObserved.CausationId.Should().Be(producer.CausationId);
    }

    [Fact]
    public async Task MetadataFactory_UnderReSeededScope_StampsCapturedCorrelation()
    {
        // Arrange: an E2E composition-root round trip through AddActa().
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddActa();
        using var provider = services.BuildServiceProvider();

        var accessor = provider.GetRequiredService<ICorrelationContextAccessor>();
        var metadataFactory = provider.GetRequiredService<Func<EventMetadata>>();

        var producer = new CorrelationContext { CorrelationId = Guid.CreateVersion7(), CausationId = Guid.CreateVersion7() };
        var foreign = new CorrelationContext { CorrelationId = Guid.CreateVersion7(), CausationId = Guid.CreateVersion7() };

        ICorrelationContext captured;
        using (accessor.BeginScope(producer))
        {
            captured = accessor.Current!;
        }

        EventMetadata? stamped = null;

        // Act: on a pooled thread carrying a foreign ambient context, re-seed the captured one
        // before the first read reaching the metadata factory.
        await Task.Run(() =>
        {
            using var foreignScope = accessor.BeginScope(foreign);
            using var reSeededScope = accessor.BeginScope(captured);
            stamped = metadataFactory();
        });

        // Assert: the stamped event carries the producer's correlation, with a fresh MessageId.
        stamped!.CorrelationId.Should().Be(producer.CorrelationId);
        stamped.CausationId.Should().Be(producer.CausationId);
        stamped.MessageId.Should().NotBe(producer.CorrelationId);
        stamped.MessageId.Should().NotBe(producer.CausationId);
    }
}
