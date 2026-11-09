using Orders.Api.Domain;
using Orders.Api.Features.Products;

namespace Orders.UnitTests;

public class LowStockTests
{
    private static Product Make(string sku, int stock) => new(Guid.NewGuid(), sku, "Widget", 10m, stock);

    [Fact]
    public void BelowStock_ExcludesThreshold_OrdersByStockThenSku()
    {
        var products = new[] { Make("B", 2), Make("A", 2), Make("C", 1), Make("D", 5) };

        var result = products.AsQueryable().BelowStock(5).Select(p => p.Sku).ToList();

        Assert.Equal(["C", "A", "B"], result);
    }

    [Fact]
    public void BelowStock_ZeroThreshold_ReturnsNothing() =>
        Assert.Empty(new[] { Make("A", 0) }.AsQueryable().BelowStock(0));
}
