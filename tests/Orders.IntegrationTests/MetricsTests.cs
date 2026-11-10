using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Orders.Api.Infrastructure;

namespace Orders.IntegrationTests;

/// <summary>The per-endpoint request metric CloudWatch gets (docs/contracts.md): route and status_class, no health checks.</summary>
public sealed class MetricsTests(ApiFixture fixture) : IDisposable
{
    private readonly ConcurrentQueue<Dictionary<string, object?>> _measurements = new();
    private readonly MeterListener _listener = new();
    private readonly FakeFeatureFlags _flags = new();

    public void Dispose() => _listener.Dispose();

    private void Listen()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == Telemetry.RequestDuration)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            _measurements.Enqueue(copy);
        });
        _listener.Start();
    }

    [Fact]
    public async Task Requests_are_measured_per_route_with_their_status_class_and_health_checks_are_not()
    {
        Listen();
        await using var factory = fixture.Factory.WithFlags(_flags.Set(Flags.FaultFailOrders, true, percent: 100));
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.Get("/api/version")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Get("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.Get($"/api/orders/{Guid.NewGuid()}")).StatusCode);
        var failed = await client.Post("/api/orders", Api.OrderBody(Guid.NewGuid(), (Guid.NewGuid(), 1)));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        // A request is measured as its context is disposed, which can be just after the client has the response.
        for (var i = 0; i < 40 && !_measurements.Any(m => Class(m) == "5xx"); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var seen = _measurements.ToList();
        Assert.Contains(seen, m => Route(m) == "/api/version" && Class(m) == "2xx");
        Assert.Contains(seen, m => Route(m) == "/api/orders/{id:guid}" && Class(m) == "4xx");
        Assert.Contains(seen, m => Route(m)?.StartsWith("/api/orders", StringComparison.Ordinal) == true && Class(m) == "5xx" && Equals(m.GetValueOrDefault("http.response.status_code"), 500));
        Assert.DoesNotContain(seen, m => Route(m)?.StartsWith("/health", StringComparison.Ordinal) == true);
        Assert.All(seen, m => Assert.NotNull(Class(m)));
    }

    [Theory]
    [InlineData(200, "2xx")]
    [InlineData(304, "3xx")]
    [InlineData(409, "4xx")]
    [InlineData(503, "5xx")]
    [InlineData(0, "5xx")]
    public void Status_class_is_the_hundreds(int status, string expected) =>
        Assert.Equal(expected, Telemetry.StatusClassOf(status));

    private static string? Route(Dictionary<string, object?> m) => m.GetValueOrDefault("http.route") as string;

    private static string? Class(Dictionary<string, object?> m) => m.GetValueOrDefault(Telemetry.StatusClass) as string;
}
