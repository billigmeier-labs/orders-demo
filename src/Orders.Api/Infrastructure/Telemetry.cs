using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Orders.Api.Features.Platform;

namespace Orders.Api.Infrastructure;

internal static class Telemetry
{
    public const string ServiceName = "orders-api";

    public const string RequestDuration = "http.server.request.duration";
    public const string StatusClass = "status_class";

    /// <summary>
    /// OpenTelemetry over OTLP, off unless OTEL_EXPORTER_OTLP_ENDPOINT is set (in ECS, the ADOT collector sidecar):
    /// traces (ASP.NET Core, HttpClient, Npgsql, which carries EF Core's queries) for X-Ray, and the request metric for
    /// CloudWatch (docs/contracts.md): <c>http.server.request.duration</c> per <c>http.route</c>, with a <c>status_class</c>
    /// attribute (2xx to 5xx) the collector can't derive itself, as a base-2 exponential histogram so CloudWatch has
    /// percentiles. That gives request rate, error rate and latency per endpoint. <c>OTEL_METRICS_EXPORTER=none</c>
    /// (previews) turns metrics off; <c>OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE</c> and
    /// <c>OTEL_METRIC_EXPORT_INTERVAL</c> are honoured. The deployment alarms use ALB metrics and need none of it.
    /// </summary>
    public static IServiceCollection AddOptionalTelemetry(this IServiceCollection services, IConfiguration configuration, AppInfo info)
    {
        if (string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return services;
        }

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(configuration["OTEL_SERVICE_NAME"] ?? ServiceName, serviceVersion: info.Version)
                .AddAttributes([
                    new("deployment.environment.name", info.Environment),
                    new("vcs.ref.head.revision", info.Commit),
                ]))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddOtlpExporter());

        if (!MetricsOff(configuration))
        {
            var delta = string.Equals(configuration["OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE"], "delta", StringComparison.OrdinalIgnoreCase);
            var interval = int.TryParse(configuration["OTEL_METRIC_EXPORT_INTERVAL"], out var ms) && ms > 0 ? ms : 60_000;
            otel.WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddView(RequestDuration, new Base2ExponentialBucketHistogramConfiguration())
                .AddOtlpExporter((_, reader) =>
                {
                    reader.TemporalityPreference = delta ? MetricReaderTemporalityPreference.Delta : MetricReaderTemporalityPreference.Cumulative;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval;
                }));
        }

        return services;
    }

    internal static bool MetricsOff(IConfiguration configuration) =>
        string.Equals(configuration["OTEL_METRICS_EXPORTER"]?.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>"2xx", "3xx", "4xx" or "5xx".</summary>
    internal static string StatusClassOf(int statusCode) => statusCode is >= 100 and < 600 ? $"{statusCode / 100}xx" : "5xx";

    /// <summary>
    /// Adds <c>status_class</c> to the request metric once the response status is known, and leaves the load balancer's
    /// health checks out of it, so they don't swamp the real endpoints. Goes first in the pipeline, outside the
    /// exception handler, so a request that failed counts as 5xx.
    /// </summary>
    public static IApplicationBuilder UseRequestMetricTags(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var metrics = context.Features.Get<IHttpMetricsTagsFeature>();
            if (metrics is not null && context.Request.Path.StartsWithSegments("/health"))
            {
                metrics.MetricsDisabled = true;
            }

            try
            {
                await next(context);
            }
            catch
            {
                metrics?.Tags.Add(new(StatusClass, "5xx"));
                throw;
            }

            metrics?.Tags.Add(new(StatusClass, StatusClassOf(context.Response.StatusCode)));
        });
}
