using Acta.Abstractions;
using Acta.InMemory;

namespace Acta.Tests.Reservations.Contracts;

/// <summary>
/// Runs the shared <see cref="ReservationStoreContractTests"/> suite (task 8.5) against the in-memory
/// backend: the exact same facts that constrain <c>PostgresReservationStore</c> constrain
/// <see cref="InMemoryReservationStore"/>.
/// </summary>
public sealed class InMemoryReservationStoreContractTests : ReservationStoreContractTests
{
    protected override ValueTask<IReservationStore> CreateStoreAsync()
        => ValueTask.FromResult<IReservationStore>(new InMemoryReservationStore());
}
