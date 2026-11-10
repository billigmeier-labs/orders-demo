using System.Net;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Orders.Api.Features.Search;
using Orders.Api.Infrastructure;

namespace Orders.IntegrationTests;

public class SearchTests(ApiFixture fixture)
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    private static string Marker(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..12];

    [Fact]
    public async Task Search_FindsProductsByNameAndSku()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var product = await _client.CreateProductAsync(name: $"Lamp {marker}");

        var byName = await SearchAsync(marker.ToUpperInvariant());
        var bySku = await SearchAsync(product.Sku.ToLowerInvariant());

        Assert.Equal([product.Id], byName.Products.Select(p => p.Id));
        Assert.Equal([product.Id], bySku.Products.Select(p => p.Id));
    }

    [Fact]
    public async Task Search_FindsOrdersByIdPrefixAndCustomerName()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var customer = await _client.CreateCustomerAsync($"Searchable {marker}");
        var product = await _client.CreateProductAsync();
        var order = await _client.CreateOrderAsync(customer.Id, (product.Id, 1));

        var byCustomer = await SearchAsync(marker);
        var byId = await SearchAsync(order.Id.ToString()[..13].ToUpperInvariant());

        Assert.Equal([order.Id], byCustomer.Orders.Select(o => o.Id));
        Assert.Contains(order.Id, byId.Orders.Select(o => o.Id));
    }

    [Fact]
    public async Task Search_WildcardsAreLiteral()
    {
        var result = await SearchAsync("%_no-such-thing_%");

        Assert.Empty(result.Products);
        Assert.Empty(result.Orders);
    }

    [Fact]
    public async Task Search_BlankQuery_ReportsTheQField()
    {
        var problem = await (await _client.Get("/api/search?q=%20%20")).ReadValidationProblemAsync();

        var error = Assert.Single(problem.Errors);
        Assert.Equal("q", error.Key);
        Assert.Equal(["The q field is required."], error.Value);
    }

    [Fact]
    public async Task Search_ProductsAreOrderedBySkuAndCappedAtTwenty()
    {
        var marker = Marker("po");
        var skus = new List<string>();
        for (var i = 0; i < 21; i++)
        {
            skus.Add((await _client.CreateProductAsync(name: $"Item {marker}")).Sku);
        }

        var result = await SearchAsync(marker);

        Assert.Equal(skus.Order(StringComparer.Ordinal).Take(20), result.Products.Select(p => p.Sku));
    }

    [Fact]
    public async Task Search_OrdersAreNewestFirstAndCappedAtTwenty()
    {
        var marker = Marker("oo");
        var customer = await _client.CreateCustomerAsync($"Orderly {marker}");
        var product = await _client.CreateProductAsync();

        var ids = new List<Guid>();
        for (var i = 0; i < 21; i++)
        {
            ids.Add((await _client.CreateOrderAsync(customer.Id, (product.Id, 1))).Id);
        }

        var result = await SearchAsync(marker);

        Assert.Equal(Enumerable.Reverse(ids).Take(20), result.Orders.Select(o => o.Id));
    }

    [Fact]
    public async Task Search_OrderSummaryHasTotalLineCountAndCustomerName()
    {
        var name = $"Solo {Marker("os")}";
        var customer = await _client.CreateCustomerAsync(name);
        var cheap = await _client.CreateProductAsync(price: 10.50m);
        var dear = await _client.CreateProductAsync(price: 2.25m);
        var order = await _client.CreateOrderAsync(customer.Id, (cheap.Id, 3), (dear.Id, 2));

        var found = Assert.Single((await SearchAsync(name)).Orders);

        Assert.Equal(order.Id, found.Id);
        Assert.Equal(customer.Id, found.CustomerId);
        Assert.Equal(name, found.CustomerName);
        Assert.Equal(36.00m, found.Total);
        Assert.Equal(2, found.LineCount);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public async Task Search_LikeSpecialCharactersMatchOnlyThemselves(string special)
    {
        var marker = Marker("lk");
        var literal = await _client.CreateProductAsync(name: $"{marker} a{special}b");
        var other = await _client.CreateProductAsync(name: $"{marker} axb");
        var customer = await _client.CreateCustomerAsync($"{marker} c{special}d");
        var otherCustomer = await _client.CreateCustomerAsync($"{marker} cxd");
        var product = await _client.CreateProductAsync();
        var order = await _client.CreateOrderAsync(customer.Id, (product.Id, 1));
        await _client.CreateOrderAsync(otherCustomer.Id, (product.Id, 1));

        var products = await SearchAsync($"a{special}b");
        var orders = await SearchAsync($"c{special}d");

        Assert.Contains(literal.Id, products.Products.Select(p => p.Id));
        Assert.DoesNotContain(other.Id, products.Products.Select(p => p.Id));
        Assert.Contains(order.Id, orders.Orders.Select(o => o.Id));
        Assert.DoesNotContain(otherCustomer.Id, orders.Orders.Select(o => o.CustomerId));
    }

    [Fact]
    public async Task Search_WithSearchV2Off_MatchesInsideWords()
    {
        var marker = Marker("zv");
        var product = await _client.CreateProductAsync(name: $"{marker}kettle");

        var result = await SearchAsync(marker[3..9]);

        Assert.Equal([product.Id], result.Products.Select(p => p.Id));
    }

    [Fact]
    public async Task Search_WithSearchV2On_MatchesWordPrefixesOnly()
    {
        await using var factory = fixture.Factory.WithFlags(new FakeFeatureFlags().Set(Flags.SearchV2, true));
        var client = factory.CreateClient();
        var marker = Marker("zw");
        var product = await client.CreateProductAsync(name: $"{marker}kettle");

        var middle = await (await client.Get($"/api/search?q={marker[3..9]}")).ReadAsync<SearchResponse>();
        var prefix = await (await client.Get($"/api/search?q={marker[..8]}")).ReadAsync<SearchResponse>();

        Assert.Empty(middle.Products);
        Assert.Equal([product.Id], prefix.Products.Select(p => p.Id));
    }

    [Fact]
    public async Task Search_OrdersWithEqualCreatedAt_AreOrderedByIdDescending()
    {
        await using var factory = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.UtcNow))));
        var client = factory.CreateClient();
        var marker = Marker("ot");
        var customer = await client.CreateCustomerAsync($"Tied {marker}");
        var product = await client.CreateProductAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            created.Add((await client.CreateOrderAsync(customer.Id, (product.Id, 1))).Id);
        }

        var result = await (await client.Get($"/api/search?q={marker}")).ReadAsync<SearchResponse>();

        Assert.Equal(created.OrderByDescending(id => id), result.Orders.Select(o => o.Id));
    }

    [Fact]
    public void Search_EndpointIsTaggedSearch()
    {
        var endpoints = fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        var search = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/api/search");

        Assert.Equal(["Search"], search.Metadata.GetMetadata<ITagsMetadata>()?.Tags);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?q=")]
    [InlineData("?q=%20%20")]
    public async Task Search_MissingQuery_Returns400(string query) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.Get($"/api/search{query}")).StatusCode);

    private async Task<SearchResponse> SearchAsync(string q) =>
        await (await _client.Get($"/api/search?q={Uri.EscapeDataString(q)}")).ReadAsync<SearchResponse>();
}
