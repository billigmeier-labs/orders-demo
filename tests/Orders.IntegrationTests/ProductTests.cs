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

    [Fact]
    public async Task LowStock_ReturnsProductsBelowThreshold_LowestFirst()
    {
        var low = await CreateProduct(1);
        var lower = await CreateProduct(0);
        var atThreshold = await CreateProduct(3);

        var result = await (await _client.Get("/api/products/low-stock?threshold=3")).ReadAsync<List<ProductResponse>>();

        Assert.Contains(low, result);
        Assert.Contains(lower, result);
        Assert.DoesNotContain(atThreshold, result);
        Assert.All(result, p => Assert.True(p.Stock < 3));
        Assert.Equal(result.OrderBy(p => p.Stock).Select(p => p.Stock), result.Select(p => p.Stock));
        Assert.True(result.IndexOf(lower) < result.IndexOf(low));
    }

    [Fact]
    public async Task LowStock_DefaultThresholdIs5()
    {
        var four = await CreateProduct(4);
        var five = await CreateProduct(5);

        var result = await (await _client.Get("/api/products/low-stock")).ReadAsync<List<ProductResponse>>();

        Assert.Contains(four, result);
        Assert.DoesNotContain(five, result);
        Assert.All(result, p => Assert.True(p.Stock < 5));
    }

    [Fact]
    public async Task LowStock_ThresholdZero_ReturnsEmptyList()
    {
        await CreateProduct(0);

        var result = await (await _client.Get("/api/products/low-stock?threshold=0")).ReadAsync<List<ProductResponse>>();

        Assert.Empty(result);
    }

    [Fact]
    public async Task LowStock_NegativeThreshold_Returns400Problem()
    {
        var problem = await (await _client.Get("/api/products/low-stock?threshold=-1")).ReadValidationProblemAsync();

        Assert.Contains(problem.Errors.Keys, k => k.Equals("threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LowStock_NonNumericThreshold_Returns400() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.Get("/api/products/low-stock?threshold=abc")).StatusCode);

    private async Task<ProductResponse> CreateProduct(int stock) =>
        await (await _client.Post("/api/products", new { sku = Api.UniqueSku(), name = "Low Stock Item", price = 1m, stock }))
            .ReadAsync<ProductResponse>(HttpStatusCode.Created);
}
