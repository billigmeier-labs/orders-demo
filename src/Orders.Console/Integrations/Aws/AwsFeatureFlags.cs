using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.AppConfig;
using Amazon.AppConfig.Model;

namespace Orders.Console.Integrations.Aws;

/// <summary>
/// Feature flags in an AppConfig feature-flag profile (AWS.AppConfig.FeatureFlags format). A toggle writes a
/// new hosted configuration version and deploys it with APPCONFIG_DEPLOYMENT_STRATEGY.
/// </summary>
public sealed class AwsFeatureFlags(IAmazonAppConfig appConfig, ConsoleOptions options) : IFeatureFlagService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Ids? _ids;

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<FeatureFlag>> GetFlagsAsync(CancellationToken ct)
    {
        var (_, doc) = await LatestAsync(ct);
        return Parse(doc);
    }

    public async Task SetFlagAsync(string name, bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var ids = await ResolveAsync(ct);
            var (version, doc) = await LatestAsync(ct);
            var updated = WithFlag(doc, name, enabled);
            var created = await appConfig.CreateHostedConfigurationVersionAsync(new CreateHostedConfigurationVersionRequest
            {
                ApplicationId = ids.Application,
                ConfigurationProfileId = ids.Profile,
                Content = new MemoryStream(Encoding.UTF8.GetBytes(updated.ToJsonString())),
                ContentType = "application/json",
                LatestVersionNumber = version,
                Description = $"demo console: {name}={enabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}",
            }, ct);
            await appConfig.StartDeploymentAsync(new StartDeploymentRequest
            {
                ApplicationId = ids.Application,
                EnvironmentId = ids.Environment,
                ConfigurationProfileId = ids.Profile,
                ConfigurationVersion = (created.VersionNumber ?? 0).ToString(CultureInfo.InvariantCulture),
                DeploymentStrategyId = ids.Strategy,
                Description = $"demo console: {name}={enabled}",
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Flags and their enabled values from an AppConfig feature-flag document.</summary>
    public static IReadOnlyList<FeatureFlag> Parse(JsonNode? doc)
    {
        var flags = doc?["flags"] as JsonObject;
        var values = doc?["values"] as JsonObject;
        if (flags is null)
        {
            return [];
        }

        return flags.Select(f => new FeatureFlag(
                f.Key,
                values?[f.Key]?["enabled"]?.GetValue<bool>() ?? false,
                f.Value?["description"]?.GetValue<string>() ?? f.Value?["name"]?.GetValue<string>()))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static JsonNode WithFlag(JsonNode? doc, string name, bool enabled)
    {
        var copy = doc?.DeepClone() ?? throw new InvalidOperationException("The feature flag profile has no content yet.");
        if (copy["flags"] is not JsonObject flags || !flags.ContainsKey(name))
        {
            throw new InvalidOperationException($"No feature flag called {name}.");
        }

        if (copy["values"] is not JsonObject values)
        {
            values = [];
            copy["values"] = values;
        }

        if (values[name] is not JsonObject value)
        {
            value = [];
            values[name] = value;
        }

        value["enabled"] = enabled;
        return copy;
    }

    private async Task<(int? Version, JsonNode? Doc)> LatestAsync(CancellationToken ct)
    {
        var ids = await ResolveAsync(ct);
        var list = await appConfig.ListHostedConfigurationVersionsAsync(new ListHostedConfigurationVersionsRequest
        {
            ApplicationId = ids.Application,
            ConfigurationProfileId = ids.Profile,
            MaxResults = 1,
        }, ct);
        var latest = list.Items?.FirstOrDefault()?.VersionNumber;
        if (latest is null)
        {
            return (null, null);
        }

        var version = await appConfig.GetHostedConfigurationVersionAsync(new GetHostedConfigurationVersionRequest
        {
            ApplicationId = ids.Application,
            ConfigurationProfileId = ids.Profile,
            VersionNumber = latest,
        }, ct);
        using var reader = new StreamReader(version.Content);
        return (latest, JsonNode.Parse(await reader.ReadToEndAsync(ct)));
    }

    private async Task<Ids> ResolveAsync(CancellationToken ct)
    {
        if (_ids is not null)
        {
            return _ids;
        }

        var apps = await appConfig.ListApplicationsAsync(new ListApplicationsRequest { MaxResults = 50 }, ct);
        var app = (apps.Items ?? []).FirstOrDefault(a => a.Name == options.AppConfigApplication || a.Id == options.AppConfigApplication)
            ?? throw new InvalidOperationException($"No AppConfig application {options.AppConfigApplication}.");
        var envs = await appConfig.ListEnvironmentsAsync(new ListEnvironmentsRequest { ApplicationId = app.Id, MaxResults = 50 }, ct);
        var env = (envs.Items ?? []).FirstOrDefault(e => e.Name == options.AppConfigEnvironment || e.Id == options.AppConfigEnvironment)
            ?? throw new InvalidOperationException($"No AppConfig environment {options.AppConfigEnvironment}.");
        var profiles = await appConfig.ListConfigurationProfilesAsync(new ListConfigurationProfilesRequest { ApplicationId = app.Id, MaxResults = 50 }, ct);
        var profile = (profiles.Items ?? []).FirstOrDefault(p => p.Name == options.AppConfigProfile || p.Id == options.AppConfigProfile)
            ?? throw new InvalidOperationException($"No AppConfig profile {options.AppConfigProfile}.");

        var strategy = options.AppConfigDeploymentStrategy;
        if (!strategy.StartsWith("AppConfig.", StringComparison.Ordinal))
        {
            var strategies = await appConfig.ListDeploymentStrategiesAsync(new ListDeploymentStrategiesRequest { MaxResults = 50 }, ct);
            strategy = (strategies.Items ?? []).FirstOrDefault(s => s.Name == strategy || s.Id == strategy)?.Id
                ?? throw new InvalidOperationException($"No AppConfig deployment strategy {strategy}.");
        }

        _ids = new Ids(app.Id, env.Id, profile.Id, strategy);
        return _ids;
    }

    private sealed record Ids(string Application, string Environment, string Profile, string Strategy);
}
