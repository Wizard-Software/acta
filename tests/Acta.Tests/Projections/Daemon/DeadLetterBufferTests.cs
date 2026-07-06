using Xunit;

using Acta.Abstractions;
using Acta.Projections.Daemon;

namespace Acta.Tests.Projections.Daemon;

/// <summary>
/// Unit tests for <see cref="DeadLetterBuffer"/> (task 5.4): the recorded <see cref="DeadLetterEntry.Error"/>
/// is exactly the exception type full name plus message (never anything else — the buffer accepts only an
/// <see cref="Exception"/>, so it structurally cannot echo an event payload), it is truncated to
/// <see cref="DeadLetterBuffer.MaxErrorLength"/>, the attempt count is stored verbatim, the ring is bounded
/// (drop-oldest at <see cref="DeadLetterBuffer.DefaultCapacity"/>), and <see cref="DeadLetterBuffer.Snapshot"/>
/// returns an independent copy.
/// </summary>
public sealed class DeadLetterBufferTests
{
    private static readonly DateTimeOffset At = new(2026, 07, 06, 12, 00, 00, TimeSpan.Zero);

    [Fact]
    public void Record_BuildsErrorFromExceptionTypeFullNameAndMessage()
    {
        var buffer = new DeadLetterBuffer();
        var ex = new InvalidOperationException("apply failed for order");

        buffer.Record("orders", tenantId: null, new GlobalPosition(7), attempts: 4, ex, At);

        var entry = buffer.Snapshot().Should().ContainSingle().Subject;
        entry.Error.Should().Be($"{typeof(InvalidOperationException).FullName}: apply failed for order");
        entry.Error.Should().StartWith(typeof(InvalidOperationException).FullName);
        entry.ProjectionName.Should().Be("orders");
        entry.TenantId.Should().BeNull();
        entry.Position.Should().Be(new GlobalPosition(7));
        entry.Attempts.Should().Be(4);
        entry.OccurredAt.Should().Be(At);
    }

    [Fact]
    public void Record_LongMessage_TruncatesErrorToMaxErrorLength()
    {
        var buffer = new DeadLetterBuffer();
        var ex = new InvalidOperationException(new string('x', DeadLetterBuffer.MaxErrorLength + 500));

        buffer.Record("orders", tenantId: null, new GlobalPosition(1), attempts: 4, ex, At);

        var entry = buffer.Snapshot().Should().ContainSingle().Subject;
        entry.Error.Length.Should().Be(DeadLetterBuffer.MaxErrorLength);
        entry.Error.Should().StartWith(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public void Record_CapturesOnlyTypeAndMessage_NoExtraDataChannel()
    {
        // The buffer's only failure input is an Exception — there is no payload/event/metadata parameter,
        // so an entry cannot carry anything beyond the exception's own type + message (ADR-008/017).
        var buffer = new DeadLetterBuffer();
        var ex = new InvalidOperationException("boom");

        buffer.Record("orders", tenantId: null, new GlobalPosition(3), attempts: 1, ex, At);

        var entry = buffer.Snapshot().Should().ContainSingle().Subject;
        entry.Error.Should().Be($"{typeof(InvalidOperationException).FullName}: boom");
    }

    [Fact]
    public void Record_StoresAttemptsVerbatim()
    {
        var buffer = new DeadLetterBuffer();

        buffer.Record("a", null, new GlobalPosition(1), attempts: 1, new InvalidOperationException(), At);
        buffer.Record("b", null, new GlobalPosition(2), attempts: 9, new InvalidOperationException(), At);

        buffer.Snapshot().Select(e => e.Attempts).Should().Equal(1, 9);
    }

    [Fact]
    public void Record_BeyondCapacity_DropsOldestKeepingMostRecent()
    {
        var buffer = new DeadLetterBuffer();
        const int overflow = 3;
        var total = DeadLetterBuffer.DefaultCapacity + overflow;

        for (var i = 1; i <= total; i++)
        {
            buffer.Record("p", null, new GlobalPosition(i), attempts: 1, new InvalidOperationException(), At);
        }

        var snapshot = buffer.Snapshot();
        snapshot.Should().HaveCount(DeadLetterBuffer.DefaultCapacity);
        // The first `overflow` entries were evicted; the oldest surviving is position overflow+1, newest is `total`.
        snapshot[0].Position.Should().Be(new GlobalPosition(overflow + 1));
        snapshot[^1].Position.Should().Be(new GlobalPosition(total));
    }

    [Fact]
    public void Snapshot_IsIndependentOfLaterRecords()
    {
        var buffer = new DeadLetterBuffer();
        buffer.Record("p", null, new GlobalPosition(1), attempts: 1, new InvalidOperationException(), At);

        var first = buffer.Snapshot();
        buffer.Record("p", null, new GlobalPosition(2), attempts: 1, new InvalidOperationException(), At);

        first.Should().ContainSingle(); // unaffected by the later Record
        buffer.Snapshot().Should().HaveCount(2);
    }

    [Fact]
    public void Record_NullOrEmptyProjectionName_Throws()
    {
        var buffer = new DeadLetterBuffer();

        Invoking(() => buffer.Record("", null, new GlobalPosition(1), 1, new InvalidOperationException(), At))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Record_NullException_Throws()
    {
        var buffer = new DeadLetterBuffer();

        Invoking(() => buffer.Record("p", null, new GlobalPosition(1), 1, null!, At))
            .Should().Throw<ArgumentNullException>();
    }
}
