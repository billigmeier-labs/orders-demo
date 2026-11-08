using Orders.Api.Data;

namespace Orders.UnitTests;

public class SeedDataTests
{
    [Fact]
    public void Generate_HasContractedCounts()
    {
        var (customers, products, orders) = SeedData.Generate();

        Assert.Equal(20, customers.Count);
        Assert.Equal(50, products.Count);
        Assert.Equal(200, orders.Count);
        Assert.All(products, p => Assert.True(p.Stock >= 0));
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        var first = SeedData.Generate();
        var second = SeedData.Generate();

        Assert.Equal(first.Orders.Select(o => (o.Id, o.Status, o.Total)), second.Orders.Select(o => (o.Id, o.Status, o.Total)));
        Assert.Equal(first.Products.Select(p => (p.Id, p.Price, p.Stock)), second.Products.Select(p => (p.Id, p.Price, p.Stock)));
    }
}
