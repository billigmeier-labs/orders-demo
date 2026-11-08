using System.Net;
using Orders.Api.Features.Search;

namespace Orders.IntegrationTests;

public class SearchTests(ApiFixture fixture)
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

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

    [Theory]
    [InlineData("")]
    [InlineData("?q=")]
    [InlineData("?q=%20%20")]
    public async Task Search_MissingQuery_Returns400(string query) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.Get($"/api/search{query}")).StatusCode);

    private async Task<SearchResponse> SearchAsync(string q) =>
        await (await _client.Get($"/api/search?q={Uri.EscapeDataString(q)}")).ReadAsync<SearchResponse>();
}
