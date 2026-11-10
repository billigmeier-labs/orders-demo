using System.Net;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task LowStock_FiltersBelowThreshold_OrderedByStockThenSku()
    {
        // Other data (seed, other tests) may also match, so assert on our own products.
        var a = await _client.CreateProductAsync(stock: 0);
        var b = await _client.CreateProductAsync(stock: 2);
        var c = await _client.CreateProductAsync(stock: 2);
        var atThreshold = await _client.CreateProductAsync(stock: 4);

        var result = await (await _client.Get("/api/products/low-stock?threshold=4")).ReadAsync<List<ProductResponse>>();

        Assert.DoesNotContain(atThreshold, result);
        Assert.All(result, p => Assert.True(p.Stock < 4));
        var mine = result.Where(p => new[] { a.Id, b.Id, c.Id }.Contains(p.Id)).ToList();
        Assert.Equal(a, mine[0]);
        Assert.Equal(new[] { b, c }.OrderBy(p => p.Sku, StringComparer.Ordinal), mine.Skip(1));
    }

    [Fact]
    public async Task LowStock_DefaultThresholdIsFive()
    {
        var below = await _client.CreateProductAsync(stock: 4);
        var equal = await _client.CreateProductAsync(stock: 5);

        var result = await (await _client.Get("/api/products/low-stock")).ReadAsync<List<ProductResponse>>();

        Assert.Contains(below, result);
        Assert.DoesNotContain(equal, result);
    }

    [Fact]
    public async Task LowStock_ZeroThreshold_ReturnsEmpty() =>
        Assert.Empty(await (await _client.Get("/api/products/low-stock?threshold=0")).ReadAsync<List<ProductResponse>>());

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task LowStock_InvalidThreshold_Returns400(string threshold) =>
        await (await _client.Get($"/api/products/low-stock?threshold={threshold}")).ReadProblemAsync(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Create_DuplicateSku_Returns409()
    {
        var sku = Api.UniqueSku();
        await (await _client.Post("/api/products", new { sku, name = "First", price = 1m, stock = 1 })).ReadAsync<ProductResponse>(HttpStatusCode.Created);

        await (await _client.Post("/api/products", new { sku, name = "Second", price = 1m, stock = 1 })).ReadProblemAsync(HttpStatusCode.Conflict);
    }

    [Fact]
    public void Endpoints_AreTaggedProducts_AndRoutedUnderApiProducts()
    {
        var endpoints = fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/products", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(5, endpoints.Count);
        Assert.All(endpoints, e => Assert.Equal(["Products"], e.Metadata.GetMetadata<ITagsMetadata>()!.Tags));
        Assert.Equal(
            ["GET /api/products/", "GET /api/products/low-stock", "GET /api/products/{id:guid}", "POST /api/products/", "POST /api/products/{id:guid}/restock"],
            endpoints.Select(e => $"{e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task List_IsOrderedBySkuAscending()
    {
        await _client.CreateProductAsync();
        await _client.CreateProductAsync();

        var all = await (await _client.Get("/api/products")).ReadAsync<List<ProductResponse>>();

        Assert.True(all.Count >= 2);
        Assert.Equal(all.OrderBy(p => p.Sku, StringComparer.Ordinal).Select(p => p.Id), all.Select(p => p.Id));
    }

    [Fact]
    public async Task LowStock_InvalidThreshold_NamesFieldAndMessage()
    {
        var problem = await (await _client.Get("/api/products/low-stock?threshold=-1")).ReadValidationProblemAsync();

        var error = Assert.Single(problem.Errors);
        Assert.Equal("threshold", error.Key);
        Assert.Equal(["Threshold must be 0 or more."], error.Value);
    }

    [Fact]
    public async Task Get_Unknown_ProblemNamesProductAndId()
    {
        var id = Guid.NewGuid();

        var problem = await (await _client.Get($"/api/products/{id}")).ReadProblemAsync(HttpStatusCode.NotFound);

        Assert.Equal("Product not found.", problem.Title);
        Assert.Equal($"No product has id {id}.", problem.Detail);
    }

    [Fact]
    public async Task Restock_Unknown_ProblemNamesProductAndId()
    {
        var id = Guid.NewGuid();

        var problem = await (await _client.Post($"/api/products/{id}/restock", new { quantity = 5 })).ReadProblemAsync(HttpStatusCode.NotFound);

        Assert.Equal("Product not found.", problem.Title);
        Assert.Equal($"No product has id {id}.", problem.Detail);
    }

    [Fact]
    public async Task Create_DuplicateSku_ProblemNamesNormalizedSku()
    {
        var sku = Api.UniqueSku();
        await _client.Post("/api/products", new { sku, name = "First", price = 1m, stock = 1 });

        var problem = await (await _client.Post("/api/products", new { sku = $" {sku.ToLowerInvariant()} ", name = "Second", price = 1m, stock = 1 }))
            .ReadProblemAsync(HttpStatusCode.Conflict);

        Assert.Equal($"A product with SKU {sku} already exists.", problem.Detail);
    }
}
