using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class UserRefTests
{
    [Fact]
    public void Constructor_ValueWithAtSign_ThrowsArgumentException()
    {
        var ex = Invoking(() => new UserRef("user@example.com")).Should().Throw<ArgumentException>().Which;

        ex.ParamName.Should().Be("value");
    }

    [Fact]
    public void Constructor_ValueOver128Chars_ThrowsArgumentException()
    {
        var tooLong = new string('a', 129);

        var ex = Invoking(() => new UserRef(tooLong)).Should().Throw<ArgumentException>().Which;

        ex.ParamName.Should().Be("value");
    }

    [Fact]
    public void Constructor_NullValue_ThrowsArgumentException()
    {
        var ex = Invoking(() => new UserRef(null!)).Should().Throw<ArgumentException>().Which;

        ex.ParamName.Should().Be("value");
    }

    [Fact]
    public void Constructor_TechnicalGuid_PreservesValue()
    {
        var guid = Guid.CreateVersion7().ToString();

        var userRef = new UserRef(guid);

        userRef.Value.Should().Be(guid);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        var guid = Guid.CreateVersion7().ToString();
        var userRef = new UserRef(guid);

        userRef.ToString().Should().Be(guid);
    }

    [Fact]
    public void Constructor_RejectedValue_MessageDoesNotLeakInput()
    {
        const string rejected = "user@example.com";

        var ex = Invoking(() => new UserRef(rejected)).Should().Throw<ArgumentException>().Which;

        ex.Message.Should().NotContain(rejected);
    }
}
