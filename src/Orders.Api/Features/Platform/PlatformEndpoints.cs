using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Orders.Api.Features.Platform;

internal static class PlatformEndpoints
{
    public const string ReadyTag = "ready";

    public static void MapPlatform(this IEndpointRouteBuilder app)
    {
        // Liveness runs no checks, so it never touches the database.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });

        app.MapGet("/api/version", (AppInfo info) => info)
            .WithTags("Platform");
    }
}
