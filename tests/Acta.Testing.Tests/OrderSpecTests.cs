using Acta.Testing;
using Acta.Testing.Tests.TestSupport;

namespace Acta.Testing.Tests;

/// <summary>
/// Dogfoods the <see cref="Spec"/> Given-When-Then harness on the realistic <see cref="Order"/>
/// aggregate (task 4.3, AK-7). The first three tests are the LITERAL getting-started acceptance
/// targets from 03-contracts.md §5 and TESTING-SPEC.md §5.3 — they MUST compile and run. The rest
/// exercise the remaining command invariants and history rehydration through the same harness.
/// </summary>
public sealed class OrderSpecTests
{
    // --- Literal acceptance targets (03-contracts.md §5, TESTING-SPEC.md §5.3) ---

    [Fact] // 03-contracts.md §5 — verbatim getting-started example
    public async Task AddLine_OnPlacedOrder_RaisesOrderLineAdded()
    {
        await Spec.For<Order>()
            .Given(new OrderPlaced("o-1", "c-1"))
            .When(o => o.AddLine("SKU-7", 2))
            .Then(new OrderLineAdded("o-1", "SKU-7", 2));
    }

    [Fact] // TESTING-SPEC.md §5.3 #1 — verbatim
    public async Task Cancel_OnPlacedOrder_RaisesOrderCancelled()
    {
        await Spec.For<Order>()
            .Given(new OrderPlaced("o-1", "c-1"))
            .When(o => o.Cancel("duplicate"))
            .Then(new OrderCancelled("o-1", "duplicate"));
    }

    [Fact] // TESTING-SPEC.md §5.3 #2 — verbatim
    public async Task Cancel_OnAlreadyCancelledOrder_ThrowsInvalidOperationException()
    {
        await Spec.For<Order>()
            .Given(new OrderPlaced("o-1", "c-1"), new OrderCancelled("o-1", "x"))
            .When(o => o.Cancel("again"))
            .ThenThrows<InvalidOperationException>();
    }

    // --- Additional dogfooding of the harness paths on Order ---

    [Fact]
    public void Place_NewOrder_RaisesOrderPlaced()
    {
        var order = Order.Place("o-1", "c-1");

        order.UncommittedEvents.Should().ContainSingle()
            .Which.Should().Be(new OrderPlaced("o-1", "c-1"));
    }

    [Fact]
    public async Task AddLine_OnCancelledOrder_ThrowsInvalidOperationException()
    {
        await Spec.For<Order>()
            .Given(new OrderPlaced("o-1", "c-1"), new OrderCancelled("o-1", "x"))
            .When(o => o.AddLine("SKU-7", 1))
            .ThenThrows<InvalidOperationException>();
    }

    [Fact]
    public async Task AddLine_NonPositiveQuantity_ThrowsArgumentOutOfRangeException()
    {
        await Spec.For<Order>()
            .Given(new OrderPlaced("o-1", "c-1"))
            .When(o => o.AddLine("SKU-7", 0))
            .ThenThrows<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AddLine_SecondLineOnHistory_RaisesOrderLineAdded()
    {
        await Spec.For<Order>()
            .Given(new OrderPlaced("o-1", "c-1"), new OrderLineAdded("o-1", "SKU-7", 2))
            .When(o => o.AddLine("SKU-9", 5))
            .Then(new OrderLineAdded("o-1", "SKU-9", 5));
    }

    [Fact]
    public async Task Given_MultipleLines_RehydratesLineCountBeforeCommand()
    {
        var lineCountBeforeCommand = -1;

        await Spec.For<Order>()
            .Given(
                new OrderPlaced("o-1", "c-1"),
                new OrderLineAdded("o-1", "SKU-7", 2),
                new OrderLineAdded("o-1", "SKU-9", 5))
            .When(o =>
            {
                lineCountBeforeCommand = o.LineCount;
                o.AddLine("SKU-3", 1);
            })
            .Then(new OrderLineAdded("o-1", "SKU-3", 1));

        lineCountBeforeCommand.Should().Be(2);
    }
}
