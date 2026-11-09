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

    private async Task<ProductResponse> CreateAsync(string prefix, int stock) =>
        await (await _client.Post("/api/products", new { sku = prefix + Api.UniqueSku(), name = prefix, price = 1m, stock }))
            .ReadAsync<ProductResponse>(HttpStatusCode.Created);

    [Fact]
    public async Task LowStock_ReturnsProductsBelowThreshold_LowestStockFirst()
    {
        var low = await CreateAsync("LOWB-", 0);
        var tieFirst = await CreateAsync("LOWC-", 1);
        var tieSecond = await CreateAsync("LOWD-", 1);
        var plenty = await CreateAsync("LOWP-", 1000);

        var result = await (await _client.Get("/api/products/low-stock?threshold=2")).ReadAsync<List<ProductResponse>>();

        Assert.DoesNotContain(plenty, result);
        Assert.All(result, p => Assert.True(p.Stock < 2));
        Assert.Equal(result.OrderBy(p => p.Stock).ThenBy(p => p.Sku, StringComparer.Ordinal).ToList(), result);
        var ours = result.Where(p => p.Id == low.Id || p.Id == tieFirst.Id || p.Id == tieSecond.Id).Select(p => p.Id).ToList();
        Assert.Equal([low.Id, tieFirst.Id, tieSecond.Id], ours);
    }

    [Fact]
    public async Task LowStock_NoThreshold_UsesDefaultOfFive()
    {
        var four = await CreateAsync("LOW4-", 4);
        var five = await CreateAsync("LOW5-", 5);

        var result = await (await _client.Get("/api/products/low-stock")).ReadAsync<List<ProductResponse>>();

        Assert.Contains(four, result);
        Assert.DoesNotContain(five, result);
    }

    [Fact]
    public async Task LowStock_ZeroThreshold_ReturnsEmptyList() =>
        Assert.Empty(await (await _client.Get("/api/products/low-stock?threshold=0")).ReadAsync<List<ProductResponse>>());

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task LowStock_InvalidThreshold_Returns400(string threshold)
    {
        var response = await _client.Get($"/api/products/low-stock?threshold={threshold}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
