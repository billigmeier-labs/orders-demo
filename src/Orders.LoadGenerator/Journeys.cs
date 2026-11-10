namespace Orders.LoadGenerator;

/// <summary>
/// What simulated users do. Each journey returns the results of its requests; any failed request
/// (5xx or no response) fails the journey.
/// </summary>
#pragma warning disable CA5394 // Random picks for load, not security.
internal sealed class Journeys(OrdersApi api, Catalog catalog, Random random)
{
    /// <summary>Product words, names and SKUs people type; a quarter are two or more words, like "desk lamp".</summary>
    internal static readonly string[] SingleWordTerms =
    [
        "lamp", "kettle", "backpack", "keyboard", "bottle", "pro", "smart", "eco", "mini", "deluxe", "travel",
        "compact", "ultra", "heavy-duty", "sku-00", "sku-0012", "sku-0042", "hopper", "turing", "lovelace", "knuth",
        "grace", "ada", "berners-lee", "liskov", "dijkstra", "torvalds",
    ];

    internal static readonly string[] MultiWordTerms =
    [
        "desk lamp", "water bottle", "travel kettle", "smart keyboard", "pro backpack", "grace hopper", "ada lovelace",
        "van rossum", "alan turing", "classic desk lamp",
    ];

    // Journey names, as in LoadPlan.Journeys; each request sends its journey in X-Journey.
    internal const string Browse = "browse-products";
    internal const string Search = "search";
    internal const string ViewOrders = "view-orders";
    internal const string CreateOrder = "create-order";
    internal const string AdvanceOrder = "advance-order";

    private static readonly string[] Statuses = ["Pending", "Paid", "Shipped", "Delivered", "Cancelled"];

    public async Task<IReadOnlyList<CallResult>> BrowseProductsAsync(CancellationToken ct)
    {
        var (list, products) = await api.GetAsync<List<Product>>(Browse, "GET /products", "api/products", ct);
        var pick = products is { Count: > 0 } ? Pick(products) : Pick(catalog.Products);
        var (one, _) = await api.GetAsync<Product>(Browse, "GET /products/{id}", $"api/products/{pick.Id}", ct);
        return [list, one];
    }

    public async Task<IReadOnlyList<CallResult>> SearchAsync(CancellationToken ct)
    {
        var (result, _) = await api.GetAsync<object>(Search, "GET /search", $"api/search?q={Uri.EscapeDataString(SearchTerm())}", ct);
        return [result];
    }

    public async Task<IReadOnlyList<CallResult>> ViewOrdersAsync(CancellationToken ct)
    {
        var url = random.Next(3) switch
        {
            0 => $"api/orders?customerId={Pick(catalog.Customers).Id}",
            1 => $"api/orders?status={Pick(Statuses)}&pageSize=10",
            _ => $"api/orders?page={random.Next(1, 4)}",
        };
        var (list, page) = await api.GetAsync<Page<OrderSummary>>(ViewOrders, "GET /orders", url, ct);
        var orderId = page is { Items.Count: > 0 } ? Pick(page.Items).Id
            : catalog.Orders.Count > 0 ? Pick(catalog.Orders).Id
            : (Guid?)null;
        if (orderId is null)
        {
            return [list];
        }

        var (one, _) = await api.GetAsync<object>(ViewOrders, "GET /orders/{id}", $"api/orders/{orderId}", ct);
        return [list, one];
    }

    public async Task<IReadOnlyList<CallResult>> CreateOrderAsync(CancellationToken ct)
    {
        var customer = Pick(catalog.Customers);
        var lines = Enumerable.Range(0, random.Next(1, 4))
            .Select(_ => Pick(catalog.Products))
            .DistinctBy(p => p.Id)
            .Select(p => new { productId = p.Id, quantity = random.Next(1, 3) })
            .ToList();
        var (result, order) = await api.PostAsync<OrderSummary>(CreateOrder, "POST /orders", "api/orders", new { customerId = customer.Id, lines }, ct);
        if (order is not null)
        {
            catalog.AddCreated(order.Id);
        }

        return [result];
    }

    /// <summary>Moves an order this run created one step on: mostly pay, ship and deliver; sometimes cancel (which restocks).</summary>
    public async Task<IReadOnlyList<CallResult>> AdvanceOrderAsync(CancellationToken ct)
    {
        if (!catalog.TryTakeCreated(out var id))
        {
            // Nothing created yet: look at an existing order instead, like a support agent would.
            if (catalog.Orders.Count == 0)
            {
                return [];
            }

            var (look, _) = await api.GetAsync<object>(AdvanceOrder, "GET /orders/{id}", $"api/orders/{Pick(catalog.Orders).Id}", ct);
            return [look];
        }

        var (current, order) = await api.GetAsync<OrderSummary>(AdvanceOrder, "GET /orders/{id}", $"api/orders/{id}", ct);
        if (order is null)
        {
            return [current];
        }

        var next = NextStatus(order.Status, random.NextDouble());
        if (next is null)
        {
            return [current];
        }

        var (changed, _) = await api.PostAsync<OrderSummary>(AdvanceOrder, "POST /orders/{id}/status", $"api/orders/{id}/status", new { status = next }, ct);
        if (next is not ("Delivered" or "Cancelled"))
        {
            catalog.ReturnCreated(id);
        }

        return [current, changed];
    }

    /// <summary>The next status a real order would take; a quarter of pending or paid orders get cancelled.</summary>
    internal static string? NextStatus(string status, double roll) => status switch
    {
        "Pending" => roll < 0.25 ? "Cancelled" : "Paid",
        "Paid" => roll < 0.25 ? "Cancelled" : "Shipped",
        "Shipped" => "Delivered",
        _ => null,
    };

    internal string SearchTerm()
    {
        var roll = random.NextDouble();
        if (roll < 0.25)
        {
            return Pick(MultiWordTerms);
        }

        if (roll < 0.40 && catalog.Orders.Count > 0)
        {
            return Pick(catalog.Orders).Id.ToString()[..8];
        }

        return Pick(SingleWordTerms);
    }

    private T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];
}
#pragma warning restore CA5394
