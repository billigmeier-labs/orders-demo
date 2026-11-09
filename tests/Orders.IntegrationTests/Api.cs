using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Features.Customers;
using Orders.Api.Features.Orders;
using Orders.Api.Features.Products;

namespace Orders.IntegrationTests;

/// <summary>Small helpers so tests read as API calls.</summary>
internal static class Api
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken))!;
    }

    public static async Task<ProblemDetails> ReadProblemAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<ProblemDetails>(Json, TestContext.Current.CancellationToken))!;
    }

    public static async Task<ValidationProblemDetails> ReadValidationProblemAsync(this HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Json, TestContext.Current.CancellationToken))!;
    }

    public static Task<HttpResponseMessage> Post(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Json, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> Get(this HttpClient client, string url) =>
        client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);

    public static async Task<CustomerResponse> CreateCustomerAsync(this HttpClient client, string? name = null) =>
        await (await client.Post("/api/customers", new { name = name ?? "Test Customer", email = $"{Guid.NewGuid():N}@example.com" }))
            .ReadAsync<CustomerResponse>(HttpStatusCode.Created);

    public static async Task<ProductResponse> CreateProductAsync(this HttpClient client, decimal price = 10m, int stock = 100, string? name = null) =>
        await (await client.Post("/api/products", new { sku = UniqueSku(), name = name ?? "Test Product", price, stock }))
            .ReadAsync<ProductResponse>(HttpStatusCode.Created);

    public static async Task<OrderResponse> CreateOrderAsync(this HttpClient client, Guid customerId, params (Guid ProductId, int Quantity)[] lines) =>
        await (await client.Post("/api/orders", OrderBody(customerId, lines))).ReadAsync<OrderResponse>(HttpStatusCode.Created);

    public static object OrderBody(Guid customerId, params (Guid ProductId, int Quantity)[] lines) =>
        OrderBody(customerId, null, lines);

    public static object OrderBody(Guid customerId, string? deliveryNotes, params (Guid ProductId, int Quantity)[] lines) =>
        new { customerId, deliveryNotes, lines = lines.Select(l => new { productId = l.ProductId, quantity = l.Quantity }) };

    public static string UniqueSku() => $"T-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
}
