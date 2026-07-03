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

        Assert.Equal(messageId, metadata.MessageId);
        Assert.Equal(correlationId, metadata.CorrelationId);
        Assert.Equal(causationId, metadata.CausationId);
        Assert.Equal(timestamp, metadata.Timestamp);
        Assert.Equal(user, metadata.User);
        Assert.Equal("tenant-1", metadata.TenantId);
        Assert.Equal("00-trace-01", metadata.TraceParent);
        Assert.Equal("state-01", metadata.TraceState);
        Assert.Same(extensions, metadata.Extensions);
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

        Assert.Equal("tenant-2", updated.TenantId);
        Assert.Equal(original.MessageId, updated.MessageId);
        Assert.Equal(original.CorrelationId, updated.CorrelationId);
        Assert.Equal(original.CausationId, updated.CausationId);
    }
}
