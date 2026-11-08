namespace Orders.Api.Domain;

internal sealed class Order
{
    private readonly List<OrderLine> _lines = [];

    public Order(Guid id, Customer customer, DateTimeOffset createdAt)
    {
        Id = id;
        Customer = customer;
        CustomerId = customer.Id;
        CreatedAt = createdAt;
        Status = OrderStatus.Pending;
    }

    private Order()
    {
        // For EF Core.
    }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public Customer Customer { get; private set; } = null!;
    public OrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public uint Version { get; private set; }
    public IReadOnlyList<OrderLine> Lines => _lines;
    public decimal Total => _lines.Sum(line => line.LineTotal);

    /// <summary>
    /// Adds a line at the product's current price and takes the quantity out of stock.
    /// Returns false, changing nothing, if there isn't enough stock.
    /// </summary>
    public bool TryAddLine(Product product, int quantity)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new InvalidOperationException("Lines can only be added to a pending order.");
        }

        if (!product.TryRemoveStock(quantity))
        {
            return false;
        }

        _lines.Add(new OrderLine(_lines.Count + 1, product, quantity));
        return true;
    }

    /// <summary>
    /// Moves the order to <paramref name="next"/> if the transition is allowed.
    /// Cancelling puts the stock back. Needs <see cref="OrderLine.Product"/> loaded.
    /// </summary>
    public bool TryChangeStatus(OrderStatus next)
    {
        if (!OrderStatusRules.CanChange(Status, next))
        {
            return false;
        }

        if (next == OrderStatus.Cancelled)
        {
            foreach (var line in _lines)
            {
                line.Product.AddStock(line.Quantity);
            }
        }

        Status = next;
        return true;
    }
}
