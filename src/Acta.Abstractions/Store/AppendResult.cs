namespace Acta.Abstractions;

/// <summary>
/// Result of an <see cref="IEventStore.AppendAsync"/> call: the stream's version after the
/// append and the all-stream position associated with it.
/// </summary>
/// <param name="NextExpectedVersion">
/// The stream's version after the append — i.e., the version of its last event. Ready to be
/// used verbatim as the <c>expectedVersion</c> guard for the caller's next append to the same
/// stream.
/// </param>
/// <param name="LastGlobalPosition">
/// The all-stream position associated with this append: the position of the last
/// newly-appended event, or, when <see cref="Deduplicated"/> is <see langword="true"/>, the
/// position of the stream's existing head (no new event was appended).
/// </param>
/// <param name="Deduplicated">
/// <see langword="true"/> when the whole batch was already present — deduplicated on
/// <c>(streamId, EventId)</c> — and nothing new was appended. This is an idempotent success,
/// not an error: a duplicate never throws, even when the optimistic-concurrency guard would
/// otherwise have failed (ADR-003, D3).
/// </param>
public sealed record AppendResult(long NextExpectedVersion, GlobalPosition LastGlobalPosition, bool Deduplicated);
