using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orders.Api.Features.Products;
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
    public async Task EqualRank_ProductsOrderBySkuAscending()
    {
        var marker = $"zt{Guid.NewGuid():N}"[..10];
        var later = await CreateProductAsync(SkuWithPrefix("BB"), $"Kettle {marker}");
        var earlier = await CreateProductAsync(SkuWithPrefix("AA"), $"Kettle {marker}");

        var result = await SearchAsync(marker);

        Assert.Equal([earlier.Id, later.Id], result.Products.Select(p => p.Id));
    }

    [Fact]
    public async Task ProductRank_CountsTheLastWordOfTheName()
    {
        var marker = $"zu{Guid.NewGuid():N}"[..10];
        // Both match once, so only the SKU tells them apart, unless the name's last word is glued to the SKU.
        var matchAtEnd = await CreateProductAsync(SkuWithPrefix("BB"), $"Compact {marker}");
        var matchAtStart = await CreateProductAsync(SkuWithPrefix("AA"), $"{marker} Compact");

        var result = await SearchAsync(marker);

        Assert.Equal([matchAtStart.Id, matchAtEnd.Id], result.Products.Select(p => p.Id));
    }

    [Fact]
    public async Task Orders_IdPrefixMatchSortsBeforeNewerNameMatch()
    {
        var product = await _client.CreateProductAsync();
        var idMatch = await _client.CreateOrderAsync((await _client.CreateCustomerAsync()).Id, (product.Id, 1));
        var term = idMatch.Id.ToString()[..13];

        // Created after the id match, with a customer name that matches the same term: still sorts after it.
        var nameMatch = await _client.CreateOrderAsync((await _client.CreateCustomerAsync(term)).Id, (product.Id, 1));

        var ids = (await SearchAsync(term)).Orders.Select(o => o.Id).ToList();

        Assert.Contains(idMatch.Id, ids);
        Assert.Contains(nameMatch.Id, ids);
        Assert.True(ids.IndexOf(idMatch.Id) < ids.IndexOf(nameMatch.Id));
    }

    [Fact]
    public async Task Orders_BetterNameMatchBeatsNewer_AndEqualRankIsNewestFirst()
    {
        var marker = $"zv{Guid.NewGuid():N}"[..10];
        var product = await _client.CreateProductAsync();
        var twice = await _client.CreateCustomerAsync($"{marker} {marker}");
        var once = await _client.CreateCustomerAsync($"{marker} Smith");
        var best = await _client.CreateOrderAsync(twice.Id, (product.Id, 1));
        var olderOnce = await _client.CreateOrderAsync(once.Id, (product.Id, 1));
        var newerOnce = await _client.CreateOrderAsync(once.Id, (product.Id, 1));

        var result = await SearchAsync(marker);

        Assert.Equal([best.Id, newerOnce.Id, olderOnce.Id], result.Orders.Select(o => o.Id));
    }

    [Fact]
    public async Task Orders_EqualRankAndCreatedAt_OrderByIdDescending()
    {
        await using var factory = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<TimeProvider>(new FixedTime(DateTimeOffset.UtcNow))));
        var client = factory.CreateClient();
        var marker = $"zw{Guid.NewGuid():N}"[..10];
        var customer = await client.CreateCustomerAsync($"{marker} Jones");
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
    public async Task OrderTotal_SumsEveryLineAtQuantityTimesUnitPrice()
    {
        var marker = $"zx{Guid.NewGuid():N}"[..10];
        var customer = await _client.CreateCustomerAsync($"{marker} Brown");
        var a = await _client.CreateProductAsync(price: 10m);
        var b = await _client.CreateProductAsync(price: 5m);
        await _client.CreateOrderAsync(customer.Id, (a.Id, 2), (b.Id, 3));

        var result = await SearchAsync(marker);

        var order = Assert.Single(result.Orders);
        Assert.Equal(35m, order.Total);
        Assert.Equal(2, order.LineCount);
    }

    private static string SkuWithPrefix(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    private async Task<ProductResponse> CreateProductAsync(string sku, string name) =>
        await (await _client.Post("/api/products", new { sku, name, price = 10m, stock = 100 }))
            .ReadAsync<ProductResponse>(HttpStatusCode.Created);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<SearchResponse> SearchAsync(string q) =>
        await (await _client.Get($"/api/search?q={Uri.EscapeDataString(q)}")).ReadAsync<SearchResponse>();
}
