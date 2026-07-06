using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Correlation;

/// <summary>
/// Unit tests for the <see cref="CorrelationContext"/> carrier record (task 6.2, Grupa 6):
/// required/optional field assignment, record value equality, and its implementation of
/// <see cref="ICorrelationContext"/>.
/// </summary>
public sealed class CorrelationContextTests
{
    [Fact]
    public void Init_AllFields_AreAssignedAndRecordEqualityHolds()
    {
        var correlationId = Guid.CreateVersion7();
        var causationId = Guid.CreateVersion7();
        var user = new UserRef("technical-account-id");

        var context = new CorrelationContext
        {
            CorrelationId = correlationId,
            CausationId = causationId,
            User = user,
            TenantId = "tenant-1",
            TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            TraceState = "vendor=value",
        };

        context.CorrelationId.Should().Be(correlationId);
        context.CausationId.Should().Be(causationId);
        context.User.Should().Be(user);
        context.TenantId.Should().Be("tenant-1");
        context.TraceParent.Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        context.TraceState.Should().Be("vendor=value");

        var equivalent = context with { };

        equivalent.Should().Be(context);
        context.Should().BeAssignableTo<ICorrelationContext>();
    }
}
