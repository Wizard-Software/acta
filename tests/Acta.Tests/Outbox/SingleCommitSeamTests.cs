using Xunit;

using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Outbox;

/// <summary>
/// AK-1 (ADR-002, FR-14): domain append and outbox enlistment through the same
/// <see cref="IEventAppendTransaction"/> become visible atomically — one all-or-nothing commit,
/// or nothing at all on rollback (dispose without commit).
/// </summary>
public sealed class SingleCommitSeamTests
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

    /// <summary>A <see cref="TimeProvider"/> that always reports a fixed, caller-supplied instant.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Commit_MakesAppendAndOutboxVisibleAtomically()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        collector.Collect("OrderPlacedIntegrationEvent", CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
            await flush.FlushAsync(tx, Ct);
            await tx.CommitAsync(Ct);
        }

        factory.OutboxState.ReadStream("order-1").Should().HaveCount(1);
        factory.OutboxState.CommittedOutbox.Should().HaveCount(1);
    }

    [Fact]
    public async Task Dispose_WithoutCommit_LeavesNothingVisible()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        collector.Collect("OrderPlacedIntegrationEvent", CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
            await flush.FlushAsync(tx, Ct);
            // No CommitAsync call — DisposeAsync at the end of this block must roll back.
        }

        factory.OutboxState.ReadStream("order-1").Should().BeEmpty();
        factory.OutboxState.CommittedOutbox.Should().BeEmpty();
    }

    [Fact]
    public async Task Commit_KeepsDomainAndIntegrationEventsSeparate()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var collector = new InMemoryIntegrationEventCollector();
        var flush = new InMemoryOutboxFlush(collector);
        collector.Collect("OrderPlacedIntegrationEvent", CreateMetadata());

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
            await flush.FlushAsync(tx, Ct);
            await tx.CommitAsync(Ct);
        }

        var streamEvents = factory.OutboxState.ReadStream("order-1");
        streamEvents.Should().OnlyContain(e => e.EventType == "TestEvent");
        factory.OutboxState.CommittedOutbox.Should().OnlyContain(e => e.Event.Equals("OrderPlacedIntegrationEvent"));
        factory.OutboxState.CommittedEventCount.Should().Be(2);
    }

    [Fact]
    public async Task Commit_WithExplicitTimeProvider_StampsStoredEventTimestamp()
    {
        var fixedTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var factory = new InMemoryEventAppendTransactionFactory(new FixedTimeProvider(fixedTime));

        await using (var tx = await factory.BeginAsync(Ct))
        {
            await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
            await tx.CommitAsync(Ct);
        }

        factory.OutboxState.ReadStream("order-1")[0].Timestamp.Should().Be(fixedTime);
    }

    [Fact]
    public async Task AppendAsync_NewStream_ReturnsCorrectNextExpectedVersionAndGlobalPosition()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        var result = await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(3), Ct);

        result.NextExpectedVersion.Should().Be(2);
        result.LastGlobalPosition.Value.Should().Be(3);
        result.Deduplicated.Should().BeFalse();
    }

    [Fact]
    public async Task AppendAsync_ExpectedVersionMismatch_ThrowsConcurrencyException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);

        var ex = (await Awaiting(
            () => tx.AppendAsync("order-1", 5, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<ConcurrencyException>()).Which;

        ex.StreamId.Should().Be("order-1");
        ex.ExpectedVersion.Should().Be(5);
        ex.ActualVersion.Should().Be(0);
    }

    [Fact]
    public async Task CommitAsync_CalledTwice_ThrowsInvalidOperationException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        await tx.CommitAsync(Ct);

        await Awaiting(() => tx.CommitAsync(Ct).AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppendAsync_AfterCommit_ThrowsInvalidOperationException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        await tx.CommitAsync(Ct);

        await Awaiting(
            () => tx.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AppendAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var tx = await factory.BeginAsync(Ct);
        await tx.DisposeAsync();

        await Awaiting(
            () => tx.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task CommitAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var tx = await factory.BeginAsync(Ct);
        await tx.DisposeAsync();

        await Awaiting(() => tx.CommitAsync(Ct).AsTask()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task AppendAsync_AfterCommit_ExceptionMessageDescribesAppendAction()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        await tx.CommitAsync(Ct);

        var ex = (await Awaiting(
            () => tx.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), Ct).AsTask())
            .Should().ThrowAsync<InvalidOperationException>()).Which;

        // Guards against a mutant that blanks the "append to" action word — the tail of the
        // message ("...has already been committed.") would otherwise still match a looser
        // Contain("commit") check even with the action word stripped out.
        ex.Message.Should().Be("Cannot append to a transaction that has already been committed.");
    }

    [Fact]
    public async Task CommitAsync_CalledTwice_ExceptionMessageDescribesCommitAction()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        await using var tx = await factory.BeginAsync(Ct);
        await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        await tx.CommitAsync(Ct);

        var ex = (await Awaiting(() => tx.CommitAsync(Ct).AsTask())
            .Should().ThrowAsync<InvalidOperationException>()).Which;

        ex.Message.Should().Be("Cannot commit a transaction that has already been committed.");
    }

    [Fact]
    public async Task AppendAsync_EmptyBatchOnTransaction_ReturnsDeduplicatedFalse()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        await using var tx = await factory.BeginAsync(Ct);

        var result = await tx.AppendAsync("order-1", ExpectedVersion.Any, [], Ct);

        result.Deduplicated.Should().BeFalse();
    }

    [Fact]
    public async Task AppendAsync_NullStreamId_ThrowsArgumentNullException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        await using var tx = await factory.BeginAsync(Ct);

        await Awaiting(() => tx.AppendAsync(null!, ExpectedVersion.Any, CreateBatch(1), Ct).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AppendAsync_EmptyStreamId_ThrowsArgumentException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        await using var tx = await factory.BeginAsync(Ct);

        await Awaiting(() => tx.AppendAsync(string.Empty, ExpectedVersion.Any, CreateBatch(1), Ct).AsTask())
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AppendAsync_NullEvents_ThrowsArgumentNullException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        await using var tx = await factory.BeginAsync(Ct);

        await Awaiting(() => tx.AppendAsync("order-1", ExpectedVersion.Any, null!, Ct).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AppendAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        await using var tx = await factory.BeginAsync(Ct);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => tx.AppendAsync("order-1", ExpectedVersion.Any, CreateBatch(1), cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BeginAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Awaiting(() => factory.BeginAsync(cts.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EnlistOutbox_NullEvents_ThrowsArgumentNullException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var tx = (InMemoryEventAppendTransaction)await factory.BeginAsync(Ct);

        Invoking(() => tx.EnlistOutbox(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task EnlistOutbox_AfterDispose_ThrowsObjectDisposedException()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var tx = (InMemoryEventAppendTransaction)await factory.BeginAsync(Ct);
        await tx.DisposeAsync();

        Invoking(() => tx.EnlistOutbox([])).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task EnlistOutbox_AfterCommit_ThrowsWithExactMessage()
    {
        var factory = new InMemoryEventAppendTransactionFactory();
        var tx = (InMemoryEventAppendTransaction)await factory.BeginAsync(Ct);
        await tx.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
        await tx.CommitAsync(Ct);

        List<CollectedIntegrationEvent> events = [new("late-event", CreateMetadata())];

        // A "Statement mutation" survivor removes the ThrowIfCommitted(...) call entirely (no
        // throw at all); a "String mutation" survivor blanks its action-word argument (wrong
        // message). The exact-message assertion below kills both.
        Invoking(() => tx.EnlistOutbox(events))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("Cannot enlist outbox entries into a transaction that has already been committed.");
    }

    [Fact]
    public async Task AppendAsync_SecondTransactionContinuesVersionSequenceAfterFirstCommits()
    {
        // Kills a mutant in InMemoryEventAppendTransaction.StreamHead that forces the "no local
        // buffer entry yet" branch to always report (-1, GlobalPosition.Start) regardless of what
        // is already committed: tx2 has no local buffer entry for "order-1" yet, so it must fall
        // back to reading the FRESHLY COMMITTED state from tx1, not a hardcoded empty stream.
        var factory = new InMemoryEventAppendTransactionFactory();

        await using (var tx1 = await factory.BeginAsync(Ct))
        {
            await tx1.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(2), Ct);
            await tx1.CommitAsync(Ct);
        }

        await using var tx2 = await factory.BeginAsync(Ct);
        var result = await tx2.AppendAsync("order-1", expectedVersion: 1, CreateBatch(1), Ct);
        await tx2.CommitAsync(Ct);

        result.NextExpectedVersion.Should().Be(2);
        var stored = factory.OutboxState.ReadStream("order-1");
        stored.Select(e => e.Version).Should().Equal(0L, 1L, 2L);
        stored.Select(e => e.GlobalPosition.Value).Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task CommitAsync_StreamCreatedByAnotherTransactionAfterBuffering_ThrowsConcurrencyExceptionAtCommitTime()
    {
        // tx1 buffers an append validated fine against the state AT BUFFER TIME (the stream does
        // not exist yet). Meanwhile tx2 creates the very same stream and commits first. Kills a
        // mutant that removes InMemoryOutboxState.Commit's re-validation of the guard against the
        // freshest committed snapshot at commit time (AK-1 concurrency safety).
        var factory = new InMemoryEventAppendTransactionFactory();

        await using var tx1 = await factory.BeginAsync(Ct);
        await tx1.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);

        await using (var tx2 = await factory.BeginAsync(Ct))
        {
            await tx2.AppendAsync("order-1", ExpectedVersion.NoStream, CreateBatch(1), Ct);
            await tx2.CommitAsync(Ct);
        }

        await Awaiting(() => tx1.CommitAsync(Ct).AsTask()).Should().ThrowAsync<ConcurrencyException>();
    }
}
