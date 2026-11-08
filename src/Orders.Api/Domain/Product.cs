namespace Orders.Api.Domain;

internal sealed class Product
{
    public Product(Guid id, string sku, string name, decimal price, int stock)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stock);
        Id = id;
        Sku = sku;
        Name = name;
        Price = price;
        Stock = stock;
    }

    public Guid Id { get; private set; }
    public string Sku { get; private set; }
    public string Name { get; private set; }
    public decimal Price { get; private set; }
    public int Stock { get; private set; }

    /// <summary>Concurrency token (PostgreSQL xmin), so two orders can't both take the last unit.</summary>
    public uint Version { get; private set; }

    /// <summary>Takes <paramref name="quantity"/> units out of stock, or returns false if there aren't enough.</summary>
    public bool TryRemoveStock(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (Stock < quantity)
        {
            return false;
        }

        Stock -= quantity;
        return true;
    }

    public void AddStock(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        Stock += quantity;
    }
}
