using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Orders.Api.Features.Platform;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

public class TelemetryTests
{
    private static readonly AppInfo Info = new("1.2.3", "abc1234", "staging");

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static ServiceProvider Build(params (string Key, string? Value)[] values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptionalTelemetry(Config(values), Info);
        return services.BuildServiceProvider();
    }

    private static (string Key, string? Value) Endpoint => ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");

    private static Dictionary<string, object> Attributes(IServiceProvider provider) =>
        provider.GetRequiredService<TracerProvider>().GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Telemetry_is_off_without_an_endpoint(string? endpoint)
    {
        var services = new ServiceCollection();
        services.AddOptionalTelemetry(Config(("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint)), Info);

        Assert.Empty(services);
    }

    [Fact]
    public void Telemetry_is_on_with_an_endpoint()
    {
        using var provider = Build(Endpoint);

        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.NotNull(provider.GetService<MeterProvider>());
    }

    [Fact]
    public void The_resource_names_the_service_and_the_release()
    {
        using var provider = Build(Endpoint);

        var attributes = Attributes(provider);
        Assert.Equal("orders-api", attributes["service.name"]);
        Assert.Equal("1.2.3", attributes["service.version"]);
        Assert.Equal("staging", attributes["deployment.environment.name"]);
        Assert.Equal("abc1234", attributes["vcs.ref.head.revision"]);
    }

    [Fact]
    public void The_service_name_comes_from_OTEL_SERVICE_NAME()
    {
        using var provider = Build(Endpoint, ("OTEL_SERVICE_NAME", "orders-api-prod"));

        Assert.Equal("orders-api-prod", Attributes(provider)["service.name"]);
    }

    [Theory]
    [InlineData("/health/live", false)]
    [InlineData("/health/ready", false)]
    [InlineData("/api/orders", true)]
    public void Health_checks_are_not_traced(string path, bool traced)
    {
        using var provider = Build(Endpoint);
        var options = provider.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>().Get(null);
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.NotNull(options.Filter);
        Assert.Equal(traced, options.Filter(context));
    }

    [Theory]
    [InlineData(null, 60_000)]
    [InlineData("abc", 60_000)]
    [InlineData("0", 60_000)]
    [InlineData("-5", 60_000)]
    [InlineData("1000", 1000)]
    public void The_metric_export_interval_is_honoured_and_defaults_to_a_minute(string? configured, int expected)
    {
        using var provider = Build(Endpoint, ("OTEL_METRIC_EXPORT_INTERVAL", configured));

        Assert.Equal(expected, Reader(provider).PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds);
    }

    [Theory]
    [InlineData(null, MetricReaderTemporalityPreference.Cumulative)]
    [InlineData("cumulative", MetricReaderTemporalityPreference.Cumulative)]
    [InlineData("delta", MetricReaderTemporalityPreference.Delta)]
    [InlineData("DELTA", MetricReaderTemporalityPreference.Delta)]
    public void The_metric_temporality_is_honoured(string? configured, MetricReaderTemporalityPreference expected)
    {
        using var provider = Build(Endpoint, ("OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE", configured));

        Assert.Equal(expected, Reader(provider).TemporalityPreference);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData(" NONE ", true)]
    [InlineData("otlp", false)]
    [InlineData(null, false)]
    public void Metrics_are_off_only_for_none(string? exporter, bool off) =>
        Assert.Equal(off, Telemetry.MetricsOff(Config(("OTEL_METRICS_EXPORTER", exporter))));

    [Fact]
    public void Metrics_can_be_turned_off()
    {
        using var provider = Build(Endpoint, ("OTEL_METRICS_EXPORTER", "none"));

        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.Null(provider.GetService<MeterProvider>());
    }

    [Theory]
    [InlineData(99, "5xx")]
    [InlineData(100, "1xx")]
    [InlineData(599, "5xx")]
    [InlineData(600, "5xx")]
    [InlineData(-1, "5xx")]
    public void Status_class_covers_exactly_100_to_599(int status, string expected) =>
        Assert.Equal(expected, Telemetry.StatusClassOf(status));

    [Fact]
    public async Task The_request_metric_gets_the_status_class_of_the_response()
    {
        var (metrics, context) = Request("/api/orders");

        await Pipeline(c => c.Response.StatusCode = 404)(context);

        Assert.False(metrics.MetricsDisabled);
        Assert.Equal(new("status_class", "4xx"), Assert.Single(metrics.Tags));
    }

    [Fact]
    public async Task A_request_that_throws_counts_as_5xx_and_the_exception_still_propagates()
    {
        var (metrics, context) = Request("/api/orders");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Pipeline(_ => throw new InvalidOperationException("boom"))(context));

        Assert.Equal(new("status_class", "5xx"), Assert.Single(metrics.Tags));
    }

    [Fact]
    public async Task Health_checks_are_left_out_of_the_request_metric()
    {
        var (metrics, context) = Request("/health/live");

        await Pipeline(_ => { })(context);

        Assert.True(metrics.MetricsDisabled);
    }

    [Fact]
    public async Task Requests_without_a_metrics_feature_still_run()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/live";
        var ran = false;

        await Pipeline(_ => ran = true)(context);

        Assert.True(ran);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Pipeline(_ => throw new InvalidOperationException())(context));
    }

    private static MetricReaderOptions Reader(IServiceProvider provider)
    {
        // The reader options are configured as the meter provider is built.
        _ = provider.GetRequiredService<MeterProvider>();
        return provider.GetRequiredService<IOptionsMonitor<MetricReaderOptions>>().Get(null);
    }

    private static (FakeMetricsTags Metrics, HttpContext Context) Request(string path)
    {
        var metrics = new FakeMetricsTags();
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Features.Set<IHttpMetricsTagsFeature>(metrics);
        return (metrics, context);
    }

    private static RequestDelegate Pipeline(Action<HttpContext> endpoint)
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseRequestMetricTags();
        app.Run(context =>
        {
            endpoint(context);
            return Task.CompletedTask;
        });
        return app.Build();
    }

    private sealed class FakeMetricsTags : IHttpMetricsTagsFeature
    {
        public ICollection<KeyValuePair<string, object?>> Tags { get; } = [];

        public bool MetricsDisabled { get; set; }
    }
}
