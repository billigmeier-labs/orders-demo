using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Api.Infrastructure;
using Serilog.Core;
using Serilog.Events;

namespace Orders.IntegrationTests;

/// <summary>What the API's request logs contain, as JSON lines in the format CloudWatch receives.</summary>
public class LoggingTests(ApiFixture fixture)
{
    private readonly CapturingSink _sink = new();
    private readonly FakeFeatureFlags _flags = new();

    private HttpClient Client() => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.AddSingleton<ILogEventSink>(_sink);
        services.RemoveAll<IFeatureFlags>();
        services.AddSingleton<IFeatureFlags>(_flags);
    })).CreateClient();

    [Fact]
    public async Task RequestLine_HasJourneyFlagsReleaseAndRoute_ButNoQueryString()
    {
        var client = Client();
        _flags.Set(Flags.SearchV2, true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/search?q=privateterm");
        request.Headers.Add(LoggingSetup.JourneyHeader, "search");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);

        var line = await _sink.WaitForAsync(l => l.RootElement.TryGetProperty("RequestPath", out var p) && p.GetString() == "/api/search" && l.RootElement.TryGetProperty("StatusCode", out _));
        var root = line.RootElement;
        Assert.Equal("search", root.GetProperty("Journey").GetString());
        Assert.Equal("on", root.GetProperty("Flags").GetProperty(Flags.SearchV2).GetString());
        Assert.Equal("off", root.GetProperty("Flags").GetProperty(Flags.FaultFailOrders).GetString());
        Assert.Equal(OrdersApiFactory.Version, root.GetProperty("AppVersion").GetString());
        Assert.Equal(OrdersApiFactory.Commit, root.GetProperty("AppCommit").GetString());
        Assert.Equal(OrdersApiFactory.Environment, root.GetProperty("AppEnvironment").GetString());
        Assert.Equal("/api/search", root.GetProperty("Route").GetString());
        Assert.Equal(200, root.GetProperty("StatusCode").GetInt32());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("RequestId").GetString()));
        Assert.Matches("^[0-9a-f]{32}$", root.GetProperty("TraceId").GetString());
        Assert.DoesNotContain(_sink.Lines, l => l.Contains("privateterm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnhandledError_IsOneLineWithTheStackAndTheJourney()
    {
        var client = Client();
        _flags.Set(Flags.FaultFailOrders, true, percent: 100);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent(new { customerId = Guid.NewGuid(), lines = new[] { new { productId = Guid.NewGuid(), quantity = 1 } } }),
        };
        request.Headers.Add(LoggingSetup.JourneyHeader, "create-order");

        Assert.Equal(HttpStatusCode.InternalServerError, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);

        var error = await _sink.WaitForAsync(l => l.RootElement.TryGetProperty("@x", out _));
        Assert.Contains("InjectedFaultException", error.RootElement.GetProperty("@x").GetString(), StringComparison.Ordinal);
        Assert.Equal("create-order", error.RootElement.GetProperty("Journey").GetString());
        Assert.Single(_sink.Lines, l => l.Contains("\"@x\"", StringComparison.Ordinal));
        await _sink.WaitForAsync(l => l.RootElement.TryGetProperty("StatusCode", out var s) && s.GetInt32() == 500);
    }

    [Fact]
    public async Task HealthChecks_AreNotLogged()
    {
        var client = Client();

        Assert.Equal(HttpStatusCode.OK, (await client.Get("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Get("/api/version")).StatusCode);

        await _sink.WaitForAsync(l => l.RootElement.TryGetProperty("RequestPath", out var p) && p.GetString() == "/api/version");
        Assert.DoesNotContain(_sink.Lines, l => l.Contains("/health/live", StringComparison.Ordinal));
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, Api.Json), System.Text.Encoding.UTF8, "application/json");

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public void Emit(LogEvent logEvent)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            LoggingSetup.JsonFormatter.Format(logEvent, writer);
            _lines.Enqueue(writer.ToString().TrimEnd('\r', '\n'));
        }

        /// <summary>The first line matching, waiting briefly: the request summary is written as the response completes.</summary>
        public async Task<JsonDocument> WaitForAsync(Func<JsonDocument, bool> match)
        {
            for (var i = 0; i < 100; i++)
            {
                foreach (var line in Lines)
                {
                    var doc = JsonDocument.Parse(line);
                    if (match(doc))
                    {
                        return doc;
                    }

                    doc.Dispose();
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            throw new Xunit.Sdk.XunitException("No matching log line. Lines:\n" + string.Join("\n", Lines));
        }
    }
}
