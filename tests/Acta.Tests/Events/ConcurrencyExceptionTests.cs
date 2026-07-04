using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class ConcurrencyExceptionTests
{
    [Fact]
    public void Constructor_SetsStreamAndVersions()
    {
        var ex = new ConcurrencyException("order-1", 5, 7);

        ex.StreamId.Should().Be("order-1");
        ex.ExpectedVersion.Should().Be(5);
        ex.ActualVersion.Should().Be(7);
    }

    [Fact]
    public void Message_MatchesBindingFormat()
    {
        var ex = new ConcurrencyException("order-1", 5, 7);

        ex.Message.Should().Be("Concurrency conflict on 'order-1': expected 5, actual 7");
    }
}
