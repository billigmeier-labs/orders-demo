namespace Orders.LoadGenerator;

/// <summary>Tops up low-stock products before a run so create-order journeys keep succeeding.</summary>
internal static class Restock
{
    public const int LowStockBelow = 20;
    public const int TargetStock = 100;

    /// <summary>The quantity that brings a product back up to <see cref="TargetStock"/>, or 0 when it has enough.</summary>
    public static int QuantityFor(int stock) => stock < LowStockBelow ? TargetStock - stock : 0;

    /// <summary>Restocks every low product and returns how many were restocked. A failed call is logged and skipped.</summary>
    public static async Task<int> RunAsync(OrdersApi api, IReadOnlyList<Product> products, Action<string> log, CancellationToken ct)
    {
        var restocked = 0;
        foreach (var product in products)
        {
            var quantity = QuantityFor(product.Stock);
            if (quantity == 0)
            {
                continue;
            }

            var (result, _) = await api.PostAsync<object>("setup", $"api/products/{product.Id}/restock", new { quantity }, ct);
            if (result.Status == 200)
            {
                restocked++;
            }
            else
            {
                var error = result.Error is null ? "" : $", {result.Error}";
                log($"Could not restock {product.Sku} (status {result.Status}{error}).");
            }
        }

        log($"Restocked {restocked} of {products.Count} products to at least {TargetStock}.");
        return restocked;
    }
}
