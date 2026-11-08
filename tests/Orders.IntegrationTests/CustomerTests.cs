using System.Net;
using Orders.Api.Features.Customers;
using Orders.Api.Features.Orders;

namespace Orders.IntegrationTests;

public class CustomerTests(ApiFixture fixture)
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task Create_ThenGetAndList_ReturnsCustomer()
    {
        var response = await _client.Post("/api/customers", new { name = " Grace Hopper ", email = $"Grace.{Guid.NewGuid():N}@Example.com" });
        var created = await response.ReadAsync<CustomerResponse>(HttpStatusCode.Created);

        Assert.Equal($"/api/customers/{created.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Grace Hopper", created.Name);
        Assert.Equal(created.Email.ToLowerInvariant(), created.Email);

        var fetched = await (await _client.Get($"/api/customers/{created.Id}")).ReadAsync<CustomerResponse>();
        Assert.Equal(created, fetched with { CreatedAt = created.CreatedAt });
        Assert.Equal(created.CreatedAt, fetched.CreatedAt, TimeSpan.FromMilliseconds(1));

        var all = await (await _client.Get("/api/customers")).ReadAsync<List<CustomerResponse>>();
        Assert.Contains(all, c => c.Id == created.Id);
    }

    [Fact]
    public async Task Get_Unknown_Returns404Problem()
    {
        var problem = await (await _client.Get($"/api/customers/{Guid.NewGuid()}")).ReadProblemAsync(HttpStatusCode.NotFound);

        Assert.Equal(404, problem.Status);
    }

    [Theory]
    [InlineData("", "a@example.com", "Name")]
    [InlineData("Ada", "not-an-email", "Email")]
    [InlineData("Ada", null, "Email")]
    public async Task Create_Invalid_Returns400(string? name, string? email, string field)
    {
        var problem = await (await _client.Post("/api/customers", new { name, email })).ReadValidationProblemAsync();

        Assert.Contains(problem.Errors.Keys, k => k.Equals(field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListOrders_ReturnsOnlyThatCustomersOrders_NewestFirstAndPaged()
    {
        var customer = await _client.CreateCustomerAsync();
        var other = await _client.CreateCustomerAsync();
        var product = await _client.CreateProductAsync();
        var first = await _client.CreateOrderAsync(customer.Id, (product.Id, 1));
        var second = await _client.CreateOrderAsync(customer.Id, (product.Id, 2));
        await _client.CreateOrderAsync(other.Id, (product.Id, 1));

        var page1 = await ListOrdersAsync(customer.Id, "?pageSize=1");
        Assert.Equal((1, 1, 2), (page1.Page, page1.PageSize, page1.TotalCount));
        Assert.Equal([second.Id], page1.Items.Select(o => o.Id));

        var page2 = await ListOrdersAsync(customer.Id, "?pageSize=1&page=2");
        Assert.Equal([first.Id], page2.Items.Select(o => o.Id));
        Assert.All(page1.Items.Concat(page2.Items), o => Assert.Equal(customer.Id, o.CustomerId));
    }

    [Fact]
    public async Task ListOrders_CustomerWithoutOrders_ReturnsEmptyPage()
    {
        var customer = await _client.CreateCustomerAsync();

        var page = await ListOrdersAsync(customer.Id, string.Empty);

        Assert.Empty(page.Items);
        Assert.Equal((1, 20, 0), (page.Page, page.PageSize, page.TotalCount));
    }

    [Fact]
    public async Task ListOrders_UnknownCustomer_Returns404Problem() =>
        await (await _client.Get($"/api/customers/{Guid.NewGuid()}/orders")).ReadProblemAsync(HttpStatusCode.NotFound);

    [Theory]
    [InlineData("?pageSize=101")]
    [InlineData("?pageSize=0")]
    [InlineData("?page=0")]
    public async Task ListOrders_InvalidQuery_Returns400(string query)
    {
        var customer = await _client.CreateCustomerAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.Get($"/api/customers/{customer.Id}/orders{query}")).StatusCode);
    }

    private async Task<PagedResponse<OrderSummaryResponse>> ListOrdersAsync(Guid customerId, string query) =>
        await (await _client.Get($"/api/customers/{customerId}/orders{query}")).ReadAsync<PagedResponse<OrderSummaryResponse>>();

    [Fact]
    public async Task Create_DuplicateEmail_Returns409()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await (await _client.Post("/api/customers", new { name = "First", email })).ReadAsync<CustomerResponse>(HttpStatusCode.Created);

        await (await _client.Post("/api/customers", new { name = "Second", email = email.ToUpperInvariant() }))
            .ReadProblemAsync(HttpStatusCode.Conflict);
    }
}
