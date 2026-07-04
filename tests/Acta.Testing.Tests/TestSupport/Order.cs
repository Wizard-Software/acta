using Acta.Abstractions;

namespace Acta.Testing.Tests.TestSupport;

/// <summary>
/// A realistic event-sourced order aggregate used to dogfood the <see cref="Acta.Testing.Spec"/>
/// harness on the getting-started examples (task 4.3, AK-7). Mirrors the sketch in
/// 03-contracts.md §5 / TESTING-SPEC.md §5.3: <see cref="Place"/> creates the order,
/// <see cref="AddLine"/> adds an order line, and <see cref="Cancel"/> cancels it. Validation lives
/// only in the command methods; <see cref="Apply"/> is total and never throws (FR-11, AK-4).
/// </summary>
public sealed class Order : AggregateRoot
{
    private bool _placed;
    private bool _cancelled;

    /// <summary>Whether the order has been cancelled.</summary>
    public bool IsCancelled => _cancelled;

    /// <summary>Number of <see cref="OrderLineAdded"/> events folded into this order so far.</summary>
    public int LineCount { get; private set; }

    /// <summary>
    /// Creating command: places a new order. Validation lives ONLY here (03-contracts.md §5).
    /// </summary>
    /// <param name="id">The order (stream) identity. Must not be null or whitespace.</param>
    /// <param name="customerId">The placing customer's identity. Must not be null or whitespace.</param>
    /// <returns>A new <see cref="Order"/> carrying a single uncommitted <see cref="OrderPlaced"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="customerId"/> is null or whitespace.</exception>
    public static Order Place(string id, string customerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        var order = new Order();
        order.Raise(new OrderPlaced(id, customerId));
        return order;
    }

    /// <summary>Command: adds a line to the order. Records an <see cref="OrderLineAdded"/> event.</summary>
    /// <param name="sku">The product SKU. Must not be null or whitespace.</param>
    /// <param name="quantity">The quantity ordered. Must be positive.</param>
    /// <exception cref="ArgumentException"><paramref name="sku"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quantity"/> is zero or negative.</exception>
    /// <exception cref="InvalidOperationException">The order has not been placed, or has already been cancelled.</exception>
    public void AddLine(string sku, int quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        if (!_placed)
        {
            throw new InvalidOperationException("Cannot add a line to an order that has not been placed.");
        }

        if (_cancelled)
        {
            throw new InvalidOperationException("Cannot add a line to a cancelled order.");
        }

        Raise(new OrderLineAdded(Id, sku, quantity));
    }

    /// <summary>Command: cancels the order. Records an <see cref="OrderCancelled"/> event.</summary>
    /// <param name="reason">Why the order is being cancelled.</param>
    /// <exception cref="InvalidOperationException">The order has not been placed, or has already been cancelled.</exception>
    public void Cancel(string reason)
    {
        if (!_placed)
        {
            throw new InvalidOperationException("Cannot cancel an order that has not been placed.");
        }

        if (_cancelled)
        {
            throw new InvalidOperationException("The order is already cancelled.");
        }

        Raise(new OrderCancelled(Id, reason));
    }

    /// <summary>Total mutator (FR-11, AK-4): folds known events; any other type is a no-op.</summary>
    protected override void Apply(object @event)
    {
        switch (@event)
        {
            case OrderPlaced placed:
                Id = placed.OrderId;
                _placed = true;
                break;
            case OrderLineAdded:
                LineCount++;
                break;
            case OrderCancelled:
                _cancelled = true;
                break;
            default:
                break;
        }
    }
}

/// <summary>Raised by <see cref="Order.Place"/> — a new order was placed.</summary>
/// <param name="OrderId">The order (stream) identity.</param>
/// <param name="CustomerId">The placing customer's identity.</param>
public sealed record OrderPlaced(string OrderId, string CustomerId);

/// <summary>Raised by <see cref="Order.AddLine"/> — a line was added to the order.</summary>
/// <param name="OrderId">The order the line belongs to.</param>
/// <param name="Sku">The product SKU.</param>
/// <param name="Quantity">The quantity ordered.</param>
public sealed record OrderLineAdded(string OrderId, string Sku, int Quantity);

/// <summary>Raised by <see cref="Order.Cancel"/> — the order was cancelled.</summary>
/// <param name="OrderId">The cancelled order's identity.</param>
/// <param name="Reason">Why the order was cancelled.</param>
public sealed record OrderCancelled(string OrderId, string Reason);
