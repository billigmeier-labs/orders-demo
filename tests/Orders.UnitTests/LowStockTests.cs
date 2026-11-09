using Orders.Api.Domain;
using Orders.Api.Features.Products;

namespace Orders.UnitTests;

public class LowStockTests
{
    private static Product P(string sku, int stock) => new(Guid.NewGuid(), sku, sku, 1m, stock);

    private static readonly Product[] Catalog = [P("C", 3), P("A", 3), P("B", 0), P("D", 5), P("E", 9)];

    private static string[] Skus(int threshold) =>
        [.. Catalog.AsQueryable().BelowStock(threshold).Select(p => p.Sku)];

    [Fact]
    public void BelowStock_FiltersStrictlyBelowThreshold() =>
        Assert.Equal(["B", "A", "C"], Skus(5));

    [Fact]
    public void BelowStock_OrdersByStockThenSku() =>
        Assert.Equal(["B", "A", "C", "D"], Skus(6));

    [Fact]
    public void BelowStock_ZeroThreshold_IsEmpty() =>
        Assert.Empty(Skus(0));

    [Fact]
    public void DefaultThreshold_IsFive() =>
        Assert.Equal(5, ProductEndpoints.DefaultLowStockThreshold);
}
