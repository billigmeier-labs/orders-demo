using System.Net;
using Orders.Api.Features.Platform;

namespace Orders.IntegrationTests;

public class PlatformTests(ApiFixture fixture)
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task Live_ReturnsHealthy()
    {
        var response = await _client.Get("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ready_DatabaseUp_ReturnsHealthy()
    {
        var response = await _client.Get("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ready_DatabaseDown_ReturnsUnhealthyWhileLiveStaysHealthy()
    {
        // Nothing listens on port 1, and MIGRATE_ON_STARTUP is off so the app still starts.
        await using var factory = new OrdersApiFactory("Host=127.0.0.1;Port=1;Database=none;Username=x;Password=x;Timeout=2")
            .WithWebHostBuilder(b => b.UseSetting("MIGRATE_ON_STARTUP", "false"));
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.Get("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Get("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Version_ReturnsValuesFromEnvironment()
    {
        var info = await (await _client.Get("/api/version")).ReadAsync<AppInfo>();

        Assert.Equal(new AppInfo(OrdersApiFactory.Version, OrdersApiFactory.Commit, OrdersApiFactory.Environment), info);
    }
}
