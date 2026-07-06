using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Subscriptions;

/// <summary>
/// Unit tests for <see cref="CheckpointFencedException"/>. The type is thrown only by the Postgres
/// sink (fencing CAS, Feature 7); these tests pin its public shape — message format and the data it
/// carries — so a consumer that catches it can rely on both.
/// </summary>
public sealed class CheckpointFencedExceptionTests
{
    [Fact]
    public void Constructor_SetsProjectionNameOwnerTokenAndInterpolatedMessage()
    {
        var ex = new CheckpointFencedException("orders-projection", "owner-123");

        ex.ProjectionName.Should().Be("orders-projection");
        ex.OwnerToken.Should().Be("owner-123");
        ex.Message.Should().Be("Checkpoint CAS failed for 'orders-projection' (owner 'owner-123') — leadership lost");
    }

    [Fact]
    public void IsAnException()
    {
        new CheckpointFencedException("p", "o").Should().BeAssignableTo<Exception>();
    }
}
