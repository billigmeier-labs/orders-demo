using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Orders.Api.Features.Platform;

namespace Orders.Api.Infrastructure;

internal static class Telemetry
{
    public const string ServiceName = "orders-api";

    /// <summary>
    /// OpenTelemetry traces (ASP.NET Core, HttpClient, Npgsql, which carries EF Core's queries),
    /// exported over OTLP. Off unless OTEL_EXPORTER_OTLP_ENDPOINT is set, e.g. to an ADOT or X-Ray collector
    /// sidecar; the deployment alarms use ALB metrics and don't need it.
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
                .AddOtlpExporter());
        // Traces only: the collector sidecar sends them to X-Ray. Metrics come from the load balancer in CloudWatch.
        return services;
    }
}
