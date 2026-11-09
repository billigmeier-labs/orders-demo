using System.Net;
using Orders.Api.Features.Products;

namespace Orders.IntegrationTests;

public class ProductTests(ApiFixture fixture)
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task Create_ThenGetAndList_ReturnsProduct()
    {
        var sku = Api.UniqueSku();
        var response = await _client.Post("/api/products", new { sku = sku.ToLowerInvariant(), name = "Desk Lamp", price = 24.99m, stock = 7 });
        var created = await response.ReadAsync<ProductResponse>(HttpStatusCode.Created);

        Assert.Equal($"/api/products/{created.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(new ProductResponse(created.Id, sku, "Desk Lamp", 24.99m, 7), created);

        Assert.Equal(created, await (await _client.Get($"/api/products/{created.Id}")).ReadAsync<ProductResponse>());

        var all = await (await _client.Get("/api/products")).ReadAsync<List<ProductResponse>>();
        Assert.Contains(created, all);
    }

    [Fact]
    public async Task Get_Unknown_Returns404Problem() =>
        await (await _client.Get($"/api/products/{Guid.NewGuid()}")).ReadProblemAsync(HttpStatusCode.NotFound);

    public static TheoryData<object, string> InvalidProducts() => new()
    {
        { new { sku = "", name = "X", price = 1m, stock = 1 }, "Sku" },
        { new { sku = "A", name = "", price = 1m, stock = 1 }, "Name" },
        { new { sku = "A", name = "X", price = 0m, stock = 1 }, "Price" },
        { new { sku = "A", name = "X", price = 1.234m, stock = 1 }, "Price" },
        { new { sku = "A", name = "X", stock = 1 }, "Price" },
        { new { sku = "A", name = "X", price = 1m, stock = -1 }, "Stock" },
        { new { sku = "A", name = "X", price = 1m }, "Stock" },
    };

    [Theory]
    [MemberData(nameof(InvalidProducts))]
    public async Task Create_Invalid_Returns400(object body, string field)
    {
        var problem = await (await _client.Post("/api/products", body)).ReadValidationProblemAsync();

        Assert.Contains(problem.Errors.Keys, k => k.Equals(field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_DuplicateSku_Returns409()
    {
        var sku = Api.UniqueSku();
        await (await _client.Post("/api/products", new { sku, name = "First", price = 1m, stock = 1 })).ReadAsync<ProductResponse>(HttpStatusCode.Created);

        await (await _client.Post("/api/products", new { sku, name = "Second", price = 1m, stock = 1 })).ReadProblemAsync(HttpStatusCode.Conflict);
    }

    private async Task<ProductResponse> CreateWithStock(string sku, int stock) =>
        await (await _client.Post("/api/products", new { sku, name = "Low Stock Item", price = 1m, stock })).ReadAsync<ProductResponse>(HttpStatusCode.Created);

    private async Task<List<ProductResponse>> LowStockWithPrefix(string url, string prefix) =>
        (await (await _client.Get(url)).ReadAsync<List<ProductResponse>>())
            .Where(p => p.Sku.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task LowStock_DefaultAndExplicitThreshold_FiltersAndOrders()
    {
        var prefix = Api.UniqueSku();
        var two = await CreateWithStock($"{prefix}-B", 2);
        var twoFirst = await CreateWithStock($"{prefix}-A", 2);
        var four = await CreateWithStock($"{prefix}-C", 4);
        var five = await CreateWithStock($"{prefix}-D", 5);

        Assert.Equal([twoFirst, two, four], await LowStockWithPrefix("/api/products/low-stock", prefix));
        Assert.Equal([twoFirst, two, four, five], await LowStockWithPrefix("/api/products/low-stock?threshold=6", prefix));
    }

    [Fact]
    public async Task LowStock_ThresholdZero_ReturnsEmptyList() =>
        Assert.Empty(await (await _client.Get("/api/products/low-stock?threshold=0")).ReadAsync<List<ProductResponse>>());

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task LowStock_InvalidThreshold_Returns400Problem(string threshold) =>
        await (await _client.Get($"/api/products/low-stock?threshold={threshold}")).ReadProblemAsync(HttpStatusCode.BadRequest);
}
