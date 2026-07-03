using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class ExpectedVersionTests
{
    [Fact]
    public void Constants_HaveBindingSentinelValues()
    {
        Assert.Equal(-2, ExpectedVersion.Any);
        Assert.Equal(-1, ExpectedVersion.NoStream);
        Assert.Equal(0, ExpectedVersion.EmptyStream);
        Assert.Equal(-3, ExpectedVersion.StreamExists);
    }
}
