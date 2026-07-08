using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local implementation of <see cref="IEventAppendTransactionFactory"/> — the
/// Tier 3 stand-in that proves the AK-1 single-commit seam without a Postgres adapter (task 8.4).
/// Every transaction begun by this factory publishes to the same shared
/// <see cref="OutboxState"/>, so a commit made by one transaction is visible to the next
/// transaction begun from this factory.
/// </summary>
/// <param name="timeProvider">
/// Clock used to stamp <see cref="StoredEvent.Timestamp"/> on every appended event, injectable
/// for deterministic tests. <see langword="null"/> (the default) resolves to
/// <see cref="TimeProvider.System"/>.
/// </param>
public sealed class InMemoryEventAppendTransactionFactory(TimeProvider? timeProvider = null) : IEventAppendTransactionFactory
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The shared committed state every transaction begun by this factory publishes to on commit.
    /// Exposed for tests to observe committed appends (<see cref="InMemoryOutboxState.ReadStream"/>)
    /// and outbox entries (<see cref="InMemoryOutboxState.CommittedOutbox"/>) without a Postgres
    /// adapter.
    /// </summary>
    public InMemoryOutboxState OutboxState { get; } = new();

    /// <inheritdoc/>
    public ValueTask<IEventAppendTransaction> BeginAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IEventAppendTransaction>(new InMemoryEventAppendTransaction(OutboxState, _timeProvider));
    }
}
