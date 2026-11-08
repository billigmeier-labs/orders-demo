using Orders.Api.Features.Customers;
using Orders.Api.Features.Orders;
using Orders.Api.Features.Products;

namespace Orders.IntegrationTests;

public class SeedDataTests(ApiFixture fixture)
{
    [Fact]
    public async Task Seed_CreatesContractedDataDeterministicallyAndOnlyOnce()
    {
        await using var first = fixture.CreateFactory("orders_seed_a", seed: true);
        await using var second = fixture.CreateFactory("orders_seed_b", seed: true);

        var a = await SnapshotAsync(first.CreateClient());
        var b = await SnapshotAsync(second.CreateClient());

        Assert.Equal((20, 50, 200), (a.Customers.Count, a.Products.Count, a.Orders.TotalCount));
        Assert.Equal(a.Customers, b.Customers);
        Assert.Equal(a.Products, b.Products);
        Assert.Equal(a.Orders.Items, b.Orders.Items);

        // Starting again on a seeded database adds nothing.
        await using var restarted = fixture.CreateFactory("orders_seed_a", seed: true);
        var again = await SnapshotAsync(restarted.CreateClient());
        Assert.Equal((20, 50, 200), (again.Customers.Count, again.Products.Count, again.Orders.TotalCount));
    }

    private static async Task<(List<CustomerResponse> Customers, List<ProductResponse> Products, PagedResponse<OrderSummaryResponse> Orders)> SnapshotAsync(HttpClient client) =>
        (await (await client.Get("/api/customers")).ReadAsync<List<CustomerResponse>>(),
         await (await client.Get("/api/products")).ReadAsync<List<ProductResponse>>(),
         await (await client.Get("/api/orders?pageSize=100")).ReadAsync<PagedResponse<OrderSummaryResponse>>());
}
