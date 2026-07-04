using Xunit;

using Acta.Abstractions;

namespace Acta.Tests.Events;

public sealed class EventMetadataTests
{
    [Fact]
    public void Init_RequiredIdsAndOptionalFields_AreAssigned()
    {
        var messageId = Guid.CreateVersion7();
        var correlationId = Guid.CreateVersion7();
        var causationId = Guid.CreateVersion7();
        var timestamp = DateTimeOffset.UtcNow;
        var user = new UserRef(Guid.CreateVersion7().ToString());
        var extensions = new Dictionary<string, string> { ["key"] = "value" };

        var metadata = new EventMetadata
        {
            MessageId = messageId,
            CorrelationId = correlationId,
            CausationId = causationId,
            Timestamp = timestamp,
            User = user,
            TenantId = "tenant-1",
            TraceParent = "00-trace-01",
            TraceState = "state-01",
            Extensions = extensions,
        };

        metadata.MessageId.Should().Be(messageId);
        metadata.CorrelationId.Should().Be(correlationId);
        metadata.CausationId.Should().Be(causationId);
        metadata.Timestamp.Should().Be(timestamp);
        metadata.User.Should().Be(user);
        metadata.TenantId.Should().Be("tenant-1");
        metadata.TraceParent.Should().Be("00-trace-01");
        metadata.TraceState.Should().Be("state-01");
        metadata.Extensions.Should().BeSameAs(extensions);
    }

    [Fact]
    public void With_OverridesSingleField_KeepsRest()
    {
        var original = new EventMetadata
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            CausationId = Guid.CreateVersion7(),
            TenantId = "tenant-1",
        };

        var updated = original with { TenantId = "tenant-2" };

        updated.TenantId.Should().Be("tenant-2");
        updated.MessageId.Should().Be(original.MessageId);
        updated.CorrelationId.Should().Be(original.CorrelationId);
        updated.CausationId.Should().Be(original.CausationId);
    }
}
