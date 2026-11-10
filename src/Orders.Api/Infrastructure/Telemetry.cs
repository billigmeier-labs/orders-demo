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

    /// <summary>
    /// OpenTelemetry over OTLP, off unless OTEL_EXPORTER_OTLP_ENDPOINT is set (in ECS, the ADOT collector sidecar):
    /// traces (ASP.NET Core, HttpClient, Npgsql, which carries EF Core's queries) for X-Ray, and metrics for
    /// CloudWatch: <c>http.server.request.duration</c> per <c>http.route</c>, method and status code gives request
    /// rate, error rate and latency per endpoint, plus Kestrel, HttpClient, Npgsql and .NET runtime metrics.
    /// The deployment alarms use ALB metrics and don't need any of it.
    /// </summary>
    public static IServiceCollection AddOptionalTelemetry(this IServiceCollection services, IConfiguration configuration, AppInfo info)
    {
        if (string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return services;
        }

        services.AddOpenTelemetry()
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
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql")
                .AddOtlpExporter());
        return services;
    }

    /// <summary>Leaves the load balancer's health checks out of the request metrics, so they don't swamp the real endpoints.</summary>
    public static IApplicationBuilder UseHealthChecksOutOfMetrics(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health") && context.Features.Get<IHttpMetricsTagsFeature>() is { } metrics)
            {
                metrics.MetricsDisabled = true;
            }

            return next(context);
        });
}
