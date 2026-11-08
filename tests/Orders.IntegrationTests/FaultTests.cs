using System.Diagnostics;
using System.Net;
using Orders.Api.Infrastructure;

namespace Orders.IntegrationTests;

public class FaultTests(ApiFixture fixture)
{
    private readonly FakeFeatureFlags _flags = new();

    [Fact]
    public async Task FailOrders_On_CreateReturns500AndTakesNoStock()
    {
        await using var factory = fixture.Factory.WithFlags(_flags);
        var client = factory.CreateClient();
        var customer = await client.CreateCustomerAsync();
        var product = await client.CreateProductAsync(stock: 5);
        _flags.Set(Flags.FaultFailOrders, true, percent: 100);

        var problem = await (await client.Post("/api/orders", Api.OrderBody(customer.Id, (product.Id, 1))))
            .ReadProblemAsync(HttpStatusCode.InternalServerError);

        Assert.Equal(500, problem.Status);
        Assert.Equal(5, (await (await client.Get($"/api/products/{product.Id}")).ReadAsync<Orders.Api.Features.Products.ProductResponse>()).Stock);
    }

    [Fact]
    public async Task FailOrders_ZeroPercentOrOff_CreatesNormally()
    {
        await using var factory = fixture.Factory.WithFlags(_flags.Set(Flags.FaultFailOrders, true, percent: 0));
        var client = factory.CreateClient();
        var customer = await client.CreateCustomerAsync();
        var product = await client.CreateProductAsync();

        await client.CreateOrderAsync(customer.Id, (product.Id, 1));
        _flags.Set(Flags.FaultFailOrders, false, percent: 100);
        await client.CreateOrderAsync(customer.Id, (product.Id, 1));
    }

    [Fact]
    public async Task FailOrders_On_OtherEndpointsUnaffected()
    {
        await using var factory = fixture.Factory.WithFlags(_flags.Set(Flags.FaultFailOrders, true, percent: 100));
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.Get("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Get("/api/search?q=lamp")).StatusCode);
    }

    [Fact]
    public async Task SlowSearch_On_AddsAboutTwoSeconds()
    {
        await using var factory = fixture.Factory.WithFlags(_flags.Set(Flags.FaultSlowSearch, true, percent: 100));
        var client = factory.CreateClient();

        var timer = Stopwatch.StartNew();
        var response = await client.Get("/api/search?q=lamp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(timer.Elapsed >= FaultInjector.SlowSearchDelay, $"Search took {timer.Elapsed}");
    }
}
