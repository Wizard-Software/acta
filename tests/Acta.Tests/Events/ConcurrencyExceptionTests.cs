using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class ConcurrencyExceptionTests
{
    [Fact]
    public void Constructor_SetsStreamAndVersions()
    {
        var ex = new ConcurrencyException("order-1", 5, 7);

        Assert.Equal("order-1", ex.StreamId);
        Assert.Equal(5, ex.ExpectedVersion);
        Assert.Equal(7, ex.ActualVersion);
    }

    [Fact]
    public void Message_MatchesBindingFormat()
    {
        var ex = new ConcurrencyException("order-1", 5, 7);

        Assert.Equal("Concurrency conflict on 'order-1': expected 5, actual 7", ex.Message);
    }
}
