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
    public async Task Restock_AddsToStockAndReturnsProduct()
    {
        var product = await _client.CreateProductAsync(stock: 3);

        var restocked = await (await _client.Post($"/api/products/{product.Id}/restock", new { quantity = 97 })).ReadAsync<ProductResponse>();

        Assert.Equal(product with { Stock = 100 }, restocked);
        Assert.Equal(restocked, await (await _client.Get($"/api/products/{product.Id}")).ReadAsync<ProductResponse>());
    }

    [Fact]
    public async Task Restock_Unknown_Returns404Problem() =>
        await (await _client.Post($"/api/products/{Guid.NewGuid()}/restock", new { quantity = 5 })).ReadProblemAsync(HttpStatusCode.NotFound);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public async Task Restock_QuantityOutOfRange_Returns400(int quantity)
    {
        var product = await _client.CreateProductAsync(stock: 3);

        var problem = await (await _client.Post($"/api/products/{product.Id}/restock", new { quantity })).ReadValidationProblemAsync();

        Assert.Contains(problem.Errors.Keys, k => k.Equals("Quantity", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, (await (await _client.Get($"/api/products/{product.Id}")).ReadAsync<ProductResponse>()).Stock);
    }

    [Fact]
    public async Task Restock_MissingQuantity_Returns400()
    {
        var product = await _client.CreateProductAsync();

        await (await _client.Post($"/api/products/{product.Id}/restock", new { })).ReadValidationProblemAsync();
    }

    [Fact]
    public async Task Restock_Concurrent_DoesNotLoseUpdates()
    {
        var product = await _client.CreateProductAsync(stock: 0);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => _client.Post($"/api/products/{product.Id}/restock", new { quantity = 7 })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(140, (await (await _client.Get($"/api/products/{product.Id}")).ReadAsync<ProductResponse>()).Stock);
    }

    [Fact]
    public async Task LowStock_ReturnsProductsBelowThresholdLowestFirst()
    {
        var low = await _client.CreateProductAsync(stock: 0);
        var mid = await _client.CreateProductAsync(stock: 2);
        var edge = await _client.CreateProductAsync(stock: 3);
        var high = await _client.CreateProductAsync(stock: 100);

        var result = await (await _client.Get("/api/products/low-stock?threshold=3")).ReadAsync<List<ProductResponse>>();

        Assert.Contains(low, result);
        Assert.Contains(mid, result);
        Assert.DoesNotContain(edge, result);
        Assert.DoesNotContain(high, result);
        Assert.All(result, p => Assert.True(p.Stock < 3));
        Assert.Equal(result.OrderBy(p => p.Stock).ThenBy(p => p.Sku, StringComparer.Ordinal).ToList(), result);
        Assert.True(result.IndexOf(low) < result.IndexOf(mid));
    }

    [Fact]
    public async Task LowStock_DefaultThresholdIsFive()
    {
        var four = await _client.CreateProductAsync(stock: 4);
        var five = await _client.CreateProductAsync(stock: 5);

        var result = await (await _client.Get("/api/products/low-stock")).ReadAsync<List<ProductResponse>>();

        Assert.Contains(four, result);
        Assert.DoesNotContain(five, result);
    }

    [Fact]
    public async Task LowStock_ZeroThreshold_ReturnsEmptyList() =>
        Assert.Empty(await (await _client.Get("/api/products/low-stock?threshold=0")).ReadAsync<List<ProductResponse>>());

    [Fact]
    public async Task LowStock_NegativeThreshold_Returns400Problem()
    {
        var problem = await (await _client.Get("/api/products/low-stock?threshold=-1")).ReadValidationProblemAsync();

        Assert.Contains(problem.Errors.Keys, k => k.Equals("threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LowStock_NonIntegerThreshold_Returns400() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.Get("/api/products/low-stock?threshold=abc")).StatusCode);

    [Fact]
    public async Task Create_DuplicateSku_Returns409()
    {
        var sku = Api.UniqueSku();
        await (await _client.Post("/api/products", new { sku, name = "First", price = 1m, stock = 1 })).ReadAsync<ProductResponse>(HttpStatusCode.Created);

        await (await _client.Post("/api/products", new { sku, name = "Second", price = 1m, stock = 1 })).ReadProblemAsync(HttpStatusCode.Conflict);
    }
}
