using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orders.LoadGenerator;

internal sealed record Customer(Guid Id, string Name);

internal sealed record Product(Guid Id, string Sku, string Name, int Stock);

internal sealed record OrderSummary(Guid Id, Guid CustomerId, string Status);

internal sealed record Page<T>(List<T> Items, int TotalCount);

/// <summary>The outcome of one request: status (0 when there was no response) and time taken.</summary>
internal readonly record struct CallResult(int Status, TimeSpan Elapsed, string? Error)
{
    /// <summary>5xx and requests that got no response are failures; 4xx answers (409 out of stock, a lost race) are expected.</summary>
    public bool Failed => Status is 0 or >= 500;
}

/// <summary>The orders API as the journeys use it. Every call is timed and counted in <see cref="Stats"/>.</summary>
internal sealed class OrdersApi(HttpClient http, LoadStats stats)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<(CallResult Result, T? Body)> GetAsync<T>(string endpoint, string url, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(new Uri(url, UriKind.Relative), ct);
            var body = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(Json, ct) : default;
            return (stats.Record(endpoint, (int)response.StatusCode, timer.Elapsed), body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return (stats.Record(endpoint, 0, timer.Elapsed, ex.GetType().Name), default);
        }
    }

    public async Task<(CallResult Result, T? Body)> PostAsync<T>(string endpoint, string url, object body, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(url, UriKind.Relative), body, Json, ct);
            var result = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(Json, ct) : default;
            return (stats.Record(endpoint, (int)response.StatusCode, timer.Elapsed), result);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return (stats.Record(endpoint, 0, timer.Elapsed, ex.GetType().Name), default);
        }
    }
}

/// <summary>Customers, products and orders the journeys pick from, fetched before the load starts.</summary>
internal sealed class Catalog
{
    private readonly ConcurrentQueue<Guid> _created = new();

    public required IReadOnlyList<Customer> Customers { get; init; }
    public required IReadOnlyList<Product> Products { get; init; }
    public required IReadOnlyList<OrderSummary> Orders { get; init; }

    /// <summary>Orders this run created, oldest first, for the advance-order journey.</summary>
    public void AddCreated(Guid id)
    {
        _created.Enqueue(id);
        while (_created.Count > 500 && _created.TryDequeue(out _))
        {
        }
    }

    public bool TryTakeCreated(out Guid id) => _created.TryDequeue(out id);

    public void ReturnCreated(Guid id) => _created.Enqueue(id);

    public static async Task<Catalog> LoadAsync(OrdersApi api, CancellationToken ct)
    {
        var (customersResult, customers) = await api.GetAsync<List<Customer>>("setup", "api/customers", ct);
        var (productsResult, products) = await api.GetAsync<List<Product>>("setup", "api/products", ct);
        var (ordersResult, orders) = await api.GetAsync<Page<OrderSummary>>("setup", "api/orders?pageSize=100", ct);
        if (customersResult.Status != (int)HttpStatusCode.OK || productsResult.Status != (int)HttpStatusCode.OK || ordersResult.Status != (int)HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Could not read the catalog (customers {customersResult.Status}, products {productsResult.Status}, orders {ordersResult.Status}).");
        }

        if (customers is not { Count: > 0 } || products is not { Count: > 0 })
        {
            throw new InvalidOperationException("The target has no customers or products. Is SEED_DATA=true?");
        }

        return new Catalog { Customers = customers, Products = products, Orders = orders?.Items ?? [] };
    }
}
