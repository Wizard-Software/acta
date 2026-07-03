using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class UserRefTests
{
    [Fact]
    public void Constructor_ValueWithAtSign_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new UserRef("user@example.com"));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void Constructor_ValueOver128Chars_ThrowsArgumentException()
    {
        var tooLong = new string('a', 129);

        var ex = Assert.Throws<ArgumentException>(() => new UserRef(tooLong));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullValue_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new UserRef(null!));

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void Constructor_TechnicalGuid_PreservesValue()
    {
        var guid = Guid.CreateVersion7().ToString();

        var userRef = new UserRef(guid);

        Assert.Equal(guid, userRef.Value);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        var guid = Guid.CreateVersion7().ToString();
        var userRef = new UserRef(guid);

        Assert.Equal(guid, userRef.ToString());
    }

    [Fact]
    public void Constructor_RejectedValue_MessageDoesNotLeakInput()
    {
        const string rejected = "user@example.com";

        var ex = Assert.Throws<ArgumentException>(() => new UserRef(rejected));

        Assert.DoesNotContain(rejected, ex.Message);
    }
}
