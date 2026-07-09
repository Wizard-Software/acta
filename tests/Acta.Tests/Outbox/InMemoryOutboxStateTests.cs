using Xunit;

using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Outbox;

/// <summary>
/// Focused coverage for <see cref="InMemoryOutboxState"/>'s commit-time guard re-validation and
/// state-folding logic (AK-1, ADR-002/ADR-003) that <see cref="SingleCommitSeamTests"/> does not
/// already exercise: the full <see cref="InMemoryOutboxState.ValidateExpectedVersion"/> guard
/// matrix, <see cref="GlobalPosition"/> continuity across multiple streams committed within one
/// transaction, and <see cref="InMemoryOutboxState.Commit"/>'s own empty-batch no-op guard (only
/// reachable via its internal <c>PendingAppend</c> surface — the public
/// <see cref="InMemoryEventAppendTransaction.AppendAsync"/> never buffers an empty batch in the
/// first place).
/// </summary>
public sealed class InMemoryOutboxStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EventMetadata CreateMetadata() => new()
    {
        MessageId = Guid.NewGuid(),
        CorrelationId = Guid.NewGuid(),
        CausationId = Guid.NewGuid(),
    };

    private static EventData CreateEventData(string eventType = "TestEvent") =>
        new(Guid.NewGuid(), eventType, 1, new byte[] { 1, 2, 3 }, CreateMetadata());

    private static EventData[] CreateBatch(int count) => [.. Enumerable.Range(0, count).Select(_ => CreateEventData())];

    [Fact]
    public async Task CommittedEventCount_MultipleStreamsWithDifferentCounts_ReturnsSumNotMax()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
            await tx.AppendAsync("order-2", ExpectedVersion.NoStream, CreateBatch(3), Ct);
            await tx.CommitAsync(Ct);
        }

        // Sum = 5; Max (the mutant) = 3 — the two must diverge for this assertion to be meaningful.
        factory.OutboxState.CommittedEventCount.Should().Be(5);
    }

    [Fact]
    public async Task CommitAsync_MultipleStreamsInSingleTransaction_GlobalPositionAdvancesAcrossStreams()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
            await tx.AppendAsync("order-2", ExpectedVersion.NoStream, CreateBatch(1), Ct);
            await tx.CommitAsync(Ct);
        }

        // A mutant that decrements (rather than advances) the running global position between
        // streams within the same Commit call would send order-2's event to a negative position.
        factory.OutboxState.ReadStream("order-1").Select(e => e.GlobalPosition.Value).Should().Equal(1L, 2L);
        factory.OutboxState.ReadStream("order-2").Single().GlobalPosition.Value.Should().Be(3L);
    }

    [Fact]
    public async Task CommitAsync_NoStreamGuardAfterExactlyOneCommittedEvent_ThrowsConcurrencyException()
    {
        // Boundary case for the streamExists computation (currentLastVersion >= 0, not > 0):
        // exactly one committed event leaves currentLastVersion == 0, which must already count as
        // "the stream exists" for NoStream's guard.
        var factory = new InMemoryEventAppendTransactionFactory();

        await using (var tx1 = await factory.BeginAsync(Ct))
        {
            await tx1.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
            await tx1.CommitAsync(Ct);
        }

        await using var tx2 = await factory.BeginAsync(Ct);
        await Awaiting(
            () => tx2.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct).AsTask())
            .Should().ThrowAsync<ConcurrencyException>();
    }

    [Fact]
    public async Task CommitAsync_EmptyStreamGuardOnBrandNewStream_Succeeds()
    {
        // GAP-2 (mirrors InMemoryEventStore): EmptyStream collapses onto NoStream here, so it must
        // succeed against a stream that does not exist yet — a mutant that un-negates the guard
        // (streamExists instead of !streamExists) would instead reject it.
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        var result = await tx.AppendAsync("order-1", ExpectedVersion.EmptyStream, CreateBatch(1), Ct);
        await tx.CommitAsync(Ct);

        result.NextExpectedVersion.Should().Be(0);
        factory.OutboxState.ReadStream("order-1").Should().HaveCount(1);
    }

    [Fact]
    public async Task CommitAsync_AnyGuard_SucceedsOnBrandNewStream()
    {
        // A mutant that forces the Any arm's guard to always-false would reject even this,
        // the least restrictive guard, on a stream that does not exist yet.
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        var result = await tx.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct);
        await tx.CommitAsync(Ct);

        result.Deduplicated.Should().BeFalse();
        factory.OutboxState.ReadStream("order-1").Should().HaveCount(1);
    }

    [Fact]
    public void Commit_PendingAppendWithEmptyEvents_SkipsGuardValidationAsANoOp()
    {
        // The public AppendAsync surface never buffers an empty-events PendingAppend (it short-
        // circuits before adding one), so Commit's own defensive "empty batch is a no-op" guard is
        // only reachable through its internal PendingAppend surface directly. Seed a stream so
        // NoStream's guard would fail if it were (wrongly) evaluated against this empty batch.
        var outboxState = new InMemoryOutboxState();
        var timeProvider = TimeProvider.System;

        var seed = new InMemoryEventAppendTransaction.PendingAppend("order-1", ExpectedVersion.NoStream, CreateBatch(1));
        outboxState.Commit([seed], [], timeProvider);

        var emptyAppend = new InMemoryEventAppendTransaction.PendingAppend("order-1", ExpectedVersion.NoStream, []);

        Invoking(() => outboxState.Commit([emptyAppend], [], timeProvider)).Should().NotThrow();
        outboxState.ReadStream("order-1").Should().HaveCount(1);
    }
}
