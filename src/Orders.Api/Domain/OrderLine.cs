namespace Orders.Api.Domain;

internal sealed class OrderLine
{
    public OrderLine(int number, Product product, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        Id = Guid.CreateVersion7();
        Number = number;
        Product = product;
        ProductId = product.Id;
        Quantity = quantity;
        UnitPrice = product.Price;
    }

    private OrderLine()
    {
        // For EF Core.
    }

    public Guid Id { get; private set; }
    /// <summary>1-based position in the order, so lines come back in the order they were placed.</summary>
    public int Number { get; private set; }

    public Guid ProductId { get; private set; }
    public Product Product { get; private set; } = null!;
    public int Quantity { get; private set; }

    /// <summary>Copied from the product when the order is placed.</summary>
    public decimal UnitPrice { get; private set; }

    public decimal LineTotal => UnitPrice * Quantity;
}
