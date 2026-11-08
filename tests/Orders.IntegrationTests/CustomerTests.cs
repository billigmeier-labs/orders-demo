using System.Net;
using Orders.Api.Features.Customers;

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
    public async Task Create_DuplicateEmail_Returns409()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await (await _client.Post("/api/customers", new { name = "First", email })).ReadAsync<CustomerResponse>(HttpStatusCode.Created);

        await (await _client.Post("/api/customers", new { name = "Second", email = email.ToUpperInvariant() }))
            .ReadProblemAsync(HttpStatusCode.Conflict);
    }
}
