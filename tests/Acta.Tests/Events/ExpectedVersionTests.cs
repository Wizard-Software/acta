using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class ExpectedVersionTests
{
    [Fact]
    public void Constants_HaveBindingSentinelValues()
    {
        ExpectedVersion.Any.Should().Be(-2);
        ExpectedVersion.NoStream.Should().Be(-1);
        ExpectedVersion.EmptyStream.Should().Be(0);
        ExpectedVersion.StreamExists.Should().Be(-3);
    }
}
