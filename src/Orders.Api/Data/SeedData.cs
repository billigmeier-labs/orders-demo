using Microsoft.EntityFrameworkCore;
using Orders.Api.Domain;

namespace Orders.Api.Data;

/// <summary>Deterministic demo data: the same 20 customers, 50 products and 200 orders every time.</summary>
internal static class SeedData
{
    public const int CustomerCount = 20;
    public const int ProductCount = 50;
    public const int OrderCount = 200;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] FirstNames =
    [
        "Ada", "Grace", "Alan", "Linus", "Margaret", "Dennis", "Barbara", "Ken", "Frances", "Edsger",
        "Radia", "Tim", "Hedy", "Donald", "Katherine", "John", "Annie", "Bjarne", "Sophie", "Guido",
    ];

    private static readonly string[] LastNames =
    [
        "Lovelace", "Hopper", "Turing", "Torvalds", "Hamilton", "Ritchie", "Liskov", "Thompson", "Allen", "Dijkstra",
        "Perlman", "Berners-Lee", "Lamarr", "Knuth", "Johnson", "McCarthy", "Easley", "Stroustrup", "Wilson", "van Rossum",
    ];

    private static readonly string[] Adjectives =
        ["Classic", "Compact", "Deluxe", "Eco", "Heavy-Duty", "Mini", "Pro", "Smart", "Travel", "Ultra"];

    private static readonly string[] Nouns = ["Backpack", "Desk Lamp", "Kettle", "Keyboard", "Water Bottle"];

    public static async Task SeedIfEmptyAsync(OrdersDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Customers.AnyAsync(cancellationToken))
        {
            return;
        }

        var (customers, products, orders) = Generate();
        db.Customers.AddRange(customers);
        db.Products.AddRange(products);
        db.Orders.AddRange(orders);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static (List<Customer> Customers, List<Product> Products, List<Order> Orders) Generate()
    {
        // A fixed seed makes every run produce identical data, IDs included.
#pragma warning disable CA5394 // Not used for security.
        var random = new Random(20261108);

        Guid NextId()
        {
            Span<byte> bytes = stackalloc byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        }

        var customers = Enumerable.Range(0, CustomerCount)
            .Select(i => new Customer(
                NextId(),
                $"{FirstNames[i]} {LastNames[i]}",
                $"{FirstNames[i].ToLowerInvariant()}.{LastNames[i].ToLowerInvariant().Replace(' ', '-')}@example.com",
                Start.AddDays(i)))
            .ToList();

        var products = Enumerable.Range(0, ProductCount)
            .Select(i => new Product(
                NextId(),
                $"SKU-{i + 1:0000}",
                $"{Adjectives[i % Adjectives.Length]} {Nouns[i / Adjectives.Length]}",
                random.Next(500, 50_000) / 100m,
                random.Next(100, 500)))
            .ToList();

        var orders = new List<Order>(OrderCount);
        for (var i = 0; i < OrderCount; i++)
        {
            var customer = customers[random.Next(customers.Count)];
            var createdAt = Start.AddDays(30).AddHours(i * 6).AddMinutes(random.Next(0, 360));
            var order = new Order(NextId(), customer, createdAt);

            var picks = products.ToArray();
            random.Shuffle(picks);
            foreach (var product in picks.Take(random.Next(1, 5)))
            {
                if (!order.TryAddLine(product, random.Next(1, 4)))
                {
                    throw new InvalidOperationException($"Seed data ran out of stock for {product.Sku}.");
                }
            }

            foreach (var status in PathTo(PickStatus(random.Next(100))))
            {
                order.TryChangeStatus(status);
            }

            orders.Add(order);
        }
#pragma warning restore CA5394

        return (customers, products, orders);
    }

    private static OrderStatus PickStatus(int roll) => roll switch
    {
        < 25 => OrderStatus.Pending,
        < 45 => OrderStatus.Paid,
        < 65 => OrderStatus.Shipped,
        < 90 => OrderStatus.Delivered,
        _ => OrderStatus.Cancelled,
    };

    private static OrderStatus[] PathTo(OrderStatus status) => status switch
    {
        OrderStatus.Paid => [OrderStatus.Paid],
        OrderStatus.Shipped => [OrderStatus.Paid, OrderStatus.Shipped],
        OrderStatus.Delivered => [OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Delivered],
        OrderStatus.Cancelled => [OrderStatus.Cancelled],
        _ => [],
    };
}
