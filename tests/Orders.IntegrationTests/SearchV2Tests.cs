using Orders.Api.Features.Search;
using Orders.Api.Infrastructure;

namespace Orders.IntegrationTests;

/// <summary>Search with the searchV2 flag on: full-text, word-prefix, ranked.</summary>
public sealed class SearchV2Tests : IAsyncDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SearchV2Tests(ApiFixture fixture)
    {
        _factory = fixture.Factory.WithFlags(new FakeFeatureFlags().Set(Flags.SearchV2, true));
        _client = _factory.CreateClient();
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task FindsProductsByWordPrefixAndSku()
    {
        var marker = $"zq{Guid.NewGuid():N}"[..10];
        var product = await _client.CreateProductAsync(name: $"Travel {marker}kettle");

        var byPrefix = await SearchAsync(marker[..6].ToUpperInvariant());
        var bySku = await SearchAsync(product.Sku.ToLowerInvariant());

        Assert.Equal([product.Id], byPrefix.Products.Select(p => p.Id));
        Assert.Equal([product.Id], bySku.Products.Select(p => p.Id));
    }

    [Fact]
    public async Task RanksBetterMatchesFirst()
    {
        var marker = $"zr{Guid.NewGuid():N}"[..10];
        var once = await _client.CreateProductAsync(name: $"Compact {marker}");
        var twice = await _client.CreateProductAsync(name: $"{marker} {marker}");

        var result = await SearchAsync(marker);

        Assert.Equal([twice.Id, once.Id], result.Products.Select(p => p.Id));
    }

    [Fact]
    public async Task FindsOrdersByCustomerNamePrefixAndIdPrefix()
    {
        var marker = $"zs{Guid.NewGuid():N}"[..10];
        var customer = await _client.CreateCustomerAsync($"Grace {marker}");
        var product = await _client.CreateProductAsync();
        var order = await _client.CreateOrderAsync(customer.Id, (product.Id, 1));

        var byCustomer = await SearchAsync(marker[..7]);
        var byId = await SearchAsync(order.Id.ToString()[..13]);

        Assert.Equal([order.Id], byCustomer.Orders.Select(o => o.Id));
        // Version 7 ids start with a timestamp, so orders other tests create in the same instant share the prefix.
        Assert.Contains(order.Id, byId.Orders.Select(o => o.Id));
    }

    [Theory]
    [InlineData("o'brien")]
    [InlineData("heavy-duty")]
    [InlineData("a&b|!c")]
    public async Task QuotesAndOperatorsInTheTerm_DoNotBreakTheQuery(string q) =>
        await SearchAsync(q);

    [Fact]
    public async Task MultiWordTerm_MatchesProductsWithEveryWord()
    {
        var marker = $"zt{Guid.NewGuid():N}"[..10];
        var product = await _client.CreateProductAsync(name: $"Desk {marker} lamp");
        await _client.CreateProductAsync(name: $"Desk {marker} chair");

        var result = await SearchAsync($"desk {marker[..6]} la");

        Assert.Equal([product.Id], result.Products.Select(p => p.Id));
    }

    [Theory]
    [InlineData("&|!")]
    [InlineData("  &  ")]
    public async Task PunctuationOnlyTerm_ReturnsEmptyResult(string q)
    {
        var result = await SearchAsync(q);

        Assert.Empty(result.Products);
        Assert.Empty(result.Orders);
    }

    private async Task<SearchResponse> SearchAsync(string q) =>
        await (await _client.Get($"/api/search?q={Uri.EscapeDataString(q)}")).ReadAsync<SearchResponse>();
}
