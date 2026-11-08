namespace Orders.Api.Features.Platform;

/// <summary>What is running, from APP_VERSION, APP_COMMIT and APP_ENVIRONMENT.</summary>
public sealed record AppInfo(string Version, string Commit, string Environment)
{
    internal static AppInfo From(IConfiguration configuration) => new(
        configuration["APP_VERSION"] ?? "0.0.0-local",
        configuration["APP_COMMIT"] ?? "unknown",
        configuration["APP_ENVIRONMENT"] ?? "local");
}
