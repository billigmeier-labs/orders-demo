using System.Net;
using Orders.Api.Domain;
using Orders.Api.Features.Orders;
using Orders.Api.Features.Products;

namespace Orders.IntegrationTests;

public class OrderTests(ApiFixture fixture)
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task Create_CopiesPricesComputesTotalAndTakesStock()
    {
        var customer = await _client.CreateCustomerAsync("Order Tester");
        var lamp = await _client.CreateProductAsync(price: 24.99m, stock: 10);
        var mug = await _client.CreateProductAsync(price: 5.50m, stock: 3);

        var response = await _client.Post("/api/orders", Api.OrderBody(customer.Id, (lamp.Id, 2), (mug.Id, 1), (lamp.Id, 1)));
        var order = await response.ReadAsync<OrderResponse>(HttpStatusCode.Created);

        Assert.Equal($"/api/orders/{order.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal("Order Tester", order.CustomerName);
        Assert.Collection(
            order.Lines,
            line => Assert.Equal((lamp.Id, 3, 24.99m, 74.97m), (line.ProductId, line.Quantity, line.UnitPrice, line.LineTotal)),
            line => Assert.Equal((mug.Id, 1, 5.50m, 5.50m), (line.ProductId, line.Quantity, line.UnitPrice, line.LineTotal)));
        Assert.Equal(80.47m, order.Total);

        Assert.Equal(7, (await GetProductAsync(lamp.Id)).Stock);
        Assert.Equal(2, (await GetProductAsync(mug.Id)).Stock);
        Assert.Equal(order, await (await _client.Get($"/api/orders/{order.Id}")).ReadAsync<OrderResponse>(), OrderComparer.Instance);
    }

    [Fact]
    public async Task Create_NotEnoughStock_Returns409AndKeepsStock()
    {
        var customer = await _client.CreateCustomerAsync();
        var plenty = await _client.CreateProductAsync(stock: 10);
        var scarce = await _client.CreateProductAsync(stock: 1);

        var problem = await (await _client.Post("/api/orders", Api.OrderBody(customer.Id, (plenty.Id, 1), (scarce.Id, 2))))
            .ReadProblemAsync(HttpStatusCode.Conflict);

        Assert.Contains(scarce.Sku, problem.Detail, StringComparison.Ordinal);
        Assert.Equal(10, (await GetProductAsync(plenty.Id)).Stock);
        Assert.Equal(1, (await GetProductAsync(scarce.Id)).Stock);
    }

    [Fact]
    public async Task Create_UnknownCustomer_Returns400()
    {
        var product = await _client.CreateProductAsync();

        var problem = await (await _client.Post("/api/orders", Api.OrderBody(Guid.NewGuid(), (product.Id, 1)))).ReadValidationProblemAsync();

        Assert.Contains("customerId", problem.Errors.Keys);
    }

    [Fact]
    public async Task Create_UnknownProduct_Returns400()
    {
        var customer = await _client.CreateCustomerAsync();

        var problem = await (await _client.Post("/api/orders", Api.OrderBody(customer.Id, (Guid.NewGuid(), 1)))).ReadValidationProblemAsync();

        Assert.Contains("lines", problem.Errors.Keys);
    }

    [Fact]
    public async Task Create_NoLines_Returns400()
    {
        var customer = await _client.CreateCustomerAsync();

        await (await _client.Post("/api/orders", Api.OrderBody(customer.Id))).ReadValidationProblemAsync();
    }

    [Fact]
    public async Task Create_ZeroQuantity_Returns400()
    {
        var customer = await _client.CreateCustomerAsync();
        var product = await _client.CreateProductAsync();

        await (await _client.Post("/api/orders", Api.OrderBody(customer.Id, (product.Id, 0)))).ReadValidationProblemAsync();
    }

    [Fact]
    public async Task Get_Unknown_Returns404Problem() =>
        await (await _client.Get($"/api/orders/{Guid.NewGuid()}")).ReadProblemAsync(HttpStatusCode.NotFound);

    [Fact]
    public async Task List_FiltersByCustomerAndStatus_NewestFirstAndPaged()
    {
        var customer = await _client.CreateCustomerAsync();
        var product = await _client.CreateProductAsync();
        var first = await _client.CreateOrderAsync(customer.Id, (product.Id, 1));
        var second = await _client.CreateOrderAsync(customer.Id, (product.Id, 2));
        var third = await _client.CreateOrderAsync(customer.Id, (product.Id, 3));
        await ChangeStatusAsync(second.Id, OrderStatus.Paid);

        var page1 = await ListAsync($"?customerId={customer.Id}&pageSize=2");
        Assert.Equal((1, 2, 3), (page1.Page, page1.PageSize, page1.TotalCount));
        Assert.Equal([third.Id, second.Id], page1.Items.Select(o => o.Id));
        Assert.Equal(30m, page1.Items[0].Total);

        var page2 = await ListAsync($"?customerId={customer.Id}&pageSize=2&page=2");
        Assert.Equal([first.Id], page2.Items.Select(o => o.Id));

        var paid = await ListAsync($"?customerId={customer.Id}&status=Paid");
        Assert.Equal([second.Id], paid.Items.Select(o => o.Id));
    }

    [Fact]
    public async Task List_DefaultsToPageSize20()
    {
        var page = await ListAsync(string.Empty);

        Assert.Equal((1, 20), (page.Page, page.PageSize));
    }

    [Theory]
    [InlineData("?pageSize=101")]
    [InlineData("?pageSize=0")]
    [InlineData("?page=0")]
    [InlineData("?status=Lost")]
    public async Task List_InvalidQuery_Returns400(string query) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.Get($"/api/orders{query}")).StatusCode);

    [Fact]
    public async Task ChangeStatus_FollowsLifecycle()
    {
        var order = await NewOrderAsync();

        foreach (var status in new[] { OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Delivered })
        {
            var updated = await (await PostStatusAsync(order.Id, status.ToString())).ReadAsync<OrderResponse>();
            Assert.Equal(status, updated.Status);
        }
    }

    [Fact]
    public async Task ChangeStatus_DisallowedTransition_Returns409()
    {
        var order = await NewOrderAsync();

        await (await PostStatusAsync(order.Id, "Shipped")).ReadProblemAsync(HttpStatusCode.Conflict);

        Assert.Equal(OrderStatus.Pending, (await (await _client.Get($"/api/orders/{order.Id}")).ReadAsync<OrderResponse>()).Status);
    }

    [Fact]
    public async Task ChangeStatus_Cancel_PutsStockBack()
    {
        var customer = await _client.CreateCustomerAsync();
        var product = await _client.CreateProductAsync(stock: 5);
        var order = await _client.CreateOrderAsync(customer.Id, (product.Id, 4));
        await ChangeStatusAsync(order.Id, OrderStatus.Paid);

        await ChangeStatusAsync(order.Id, OrderStatus.Cancelled);

        Assert.Equal(5, (await GetProductAsync(product.Id)).Stock);
        await (await PostStatusAsync(order.Id, "Paid")).ReadProblemAsync(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ChangeStatus_UnknownOrder_Returns404() =>
        await (await PostStatusAsync(Guid.NewGuid(), "Paid")).ReadProblemAsync(HttpStatusCode.NotFound);

    [Theory]
    [InlineData(null)]
    [InlineData("Lost")]
    public async Task ChangeStatus_InvalidStatus_Returns400(string? status)
    {
        var order = await NewOrderAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PostStatusAsync(order.Id, status)).StatusCode);
    }

    private async Task<OrderResponse> NewOrderAsync()
    {
        var customer = await _client.CreateCustomerAsync();
        var product = await _client.CreateProductAsync();
        return await _client.CreateOrderAsync(customer.Id, (product.Id, 1));
    }

    private Task<HttpResponseMessage> PostStatusAsync(Guid orderId, string? status) =>
        _client.Post($"/api/orders/{orderId}/status", new { status });

    private async Task ChangeStatusAsync(Guid orderId, OrderStatus status) =>
        Assert.Equal(status, (await (await PostStatusAsync(orderId, status.ToString())).ReadAsync<OrderResponse>()).Status);

    private async Task<PagedResponse<OrderSummaryResponse>> ListAsync(string query) =>
        await (await _client.Get($"/api/orders{query}")).ReadAsync<PagedResponse<OrderSummaryResponse>>();

    private async Task<ProductResponse> GetProductAsync(Guid id) =>
        await (await _client.Get($"/api/products/{id}")).ReadAsync<ProductResponse>();

    /// <summary>Compares orders by value; records compare lists by reference.</summary>
    private sealed class OrderComparer : IEqualityComparer<OrderResponse>
    {
        public static readonly OrderComparer Instance = new();

        public bool Equals(OrderResponse? x, OrderResponse? y) =>
            x is not null && y is not null
            && (x.Id, x.CustomerId, x.CustomerName, x.Status, x.Total) == (y.Id, y.CustomerId, y.CustomerName, y.Status, y.Total)
            && (x.CreatedAt - y.CreatedAt).Duration() < TimeSpan.FromMilliseconds(1)
            && x.Lines.SequenceEqual(y.Lines);

        public int GetHashCode(OrderResponse obj) => obj.Id.GetHashCode();
    }
}
