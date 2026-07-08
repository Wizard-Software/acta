using Acta.Abstractions;

namespace Acta.InMemory;

/// <summary>
/// In-memory, process-local implementation of <see cref="IIntegrationEventCollector"/> — the
/// Tier 3 stand-in used by <c>AddActa()</c> and by the AK-1 single-commit seam tests.
/// <para>
/// Scoped per command handling: <see cref="Collect"/> appends to an internal buffer; <see cref="Drain"/>
/// returns a snapshot of that buffer, in collection order, and clears it — a subsequent
/// <see cref="Drain"/> call returns an empty list until <see cref="Collect"/> is called again.
/// A plain <see cref="Lock"/> is sufficient here: the buffer is small and short-lived (one
/// command), and this type carries no assumption about which thread calls it.
/// </para>
/// </summary>
public sealed class InMemoryIntegrationEventCollector : IIntegrationEventCollector
{
    private readonly Lock _gate = new();
    private readonly List<CollectedIntegrationEvent> _buffer = [];

    /// <inheritdoc/>
    public void Collect(object integrationEvent, EventMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentNullException.ThrowIfNull(metadata);

        lock (_gate)
        {
            _buffer.Add(new CollectedIntegrationEvent(integrationEvent, metadata));
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<CollectedIntegrationEvent> Drain()
    {
        lock (_gate)
        {
            if (_buffer.Count == 0)
            {
                return [];
            }

            var drained = _buffer.ToArray();
            _buffer.Clear();
            return drained;
        }
    }
}
