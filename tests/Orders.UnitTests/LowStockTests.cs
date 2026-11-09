using Orders.Api.Domain;
using Orders.Api.Features.Products;

namespace Orders.UnitTests;

public class LowStockTests
{
    [Fact]
    public void TryParseThreshold_Missing_UsesDefaultOfFive()
    {
        Assert.True(LowStock.TryParseThreshold(null, out var threshold));
        Assert.Equal(5, threshold);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("12", 12)]
    public void TryParseThreshold_NonNegativeInteger_IsAccepted(string raw, int expected)
    {
        Assert.True(LowStock.TryParseThreshold(raw, out var threshold));
        Assert.Equal(expected, threshold);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public void TryParseThreshold_NegativeOrNotANumber_IsRejected(string raw) =>
        Assert.False(LowStock.TryParseThreshold(raw, out _));

    private static Product P(string sku, int stock) => new(Guid.NewGuid(), sku, sku, 1m, stock);

    [Fact]
    public void Query_UsesStrictLessThan()
    {
        var result = LowStock.Query(new[] { P("A", 4), P("B", 5), P("C", 6) }.AsQueryable(), 5).ToList();

        Assert.Equal(["A"], result.Select(p => p.Sku));
    }

    [Fact]
    public void Query_SortsByStockThenSku()
    {
        var products = new[] { P("C", 2), P("B", 1), P("A", 2), P("D", 0) };

        var result = LowStock.Query(products.AsQueryable(), 5).ToList();

        Assert.Equal(["D", "B", "A", "C"], result.Select(p => p.Sku));
    }

    [Fact]
    public void Query_ThresholdZero_IsEmpty() =>
        Assert.Empty(LowStock.Query(new[] { P("A", 0), P("B", 1) }.AsQueryable(), 0));
}
