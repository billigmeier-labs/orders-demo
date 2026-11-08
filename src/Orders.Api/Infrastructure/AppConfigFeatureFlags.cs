using System.Text.Json;
using Amazon.AppConfigData;
using Amazon.AppConfigData.Model;

namespace Orders.Api.Infrastructure;

/// <summary>Where the flags live in AppConfig: APPCONFIG_APPLICATION, APPCONFIG_ENVIRONMENT, APPCONFIG_PROFILE.</summary>
internal sealed record AppConfigSettings(string Application, string Environment, string Profile)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    public static AppConfigSettings? From(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration["APPCONFIG_APPLICATION"])
            ? null
            : new(
                configuration["APPCONFIG_APPLICATION"]!,
                configuration["APPCONFIG_ENVIRONMENT"] ?? "prod",
                configuration["APPCONFIG_PROFILE"] ?? "feature-flags");
}

/// <summary>
/// The latest AppConfig feature flags, kept current by <see cref="AppConfigPoller"/>. Until the first
/// poll succeeds every flag is off, so the API starts (and serves) even when AppConfig is unreachable.
/// </summary>
internal sealed class AppConfigFeatureFlags : IFeatureFlags
{
    private volatile IReadOnlyDictionary<string, FeatureFlag> _flags = new Dictionary<string, FeatureFlag>();

    public FeatureFlag GetFlag(string flag) => _flags.GetValueOrDefault(flag) ?? FeatureFlag.Off;

    public void Update(IReadOnlyDictionary<string, FeatureFlag> flags) => _flags = flags;

    /// <summary>
    /// Parses what GetLatestConfiguration returns for an AWS.AppConfig.FeatureFlags profile:
    /// <c>{ "flagName": { "enabled": true, "percent": 20 }, ... }</c>. Also accepts the hosted
    /// document format (<c>{ "flags": {...}, "values": {...} }</c>) that the console writes.
    /// </summary>
    public static Dictionary<string, FeatureFlag> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Feature flag content must be a JSON object.");
        }

        if (root.TryGetProperty("flags", out var defined) && defined.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            root = values;
        }

        var flags = new Dictionary<string, FeatureFlag>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var enabled = false;
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in property.Value.EnumerateObject())
            {
                if (field.NameEquals("enabled"))
                {
                    enabled = field.Value.ValueKind == JsonValueKind.True;
                }
                else if (field.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    attributes[field.Name] = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : field.Value.GetRawText();
                }
            }

            flags[property.Name] = new FeatureFlag(enabled, attributes);
        }

        return flags;
    }
}

/// <summary>
/// Polls AppConfigData every <see cref="AppConfigSettings.PollInterval"/> (or what AppConfig asks for) and
/// caches the result in <see cref="AppConfigFeatureFlags"/>. GetLatestConfiguration returns content only
/// when it changed, so an empty response keeps the cached flags.
/// </summary>
internal sealed partial class AppConfigPoller(
    IAmazonAppConfigData client,
    AppConfigSettings settings,
    AppConfigFeatureFlags flags,
    ILogger<AppConfigPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? token = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = AppConfigSettings.PollInterval;
            try
            {
                token ??= (await client.StartConfigurationSessionAsync(new StartConfigurationSessionRequest
                {
                    ApplicationIdentifier = settings.Application,
                    EnvironmentIdentifier = settings.Environment,
                    ConfigurationProfileIdentifier = settings.Profile,
                    RequiredMinimumPollIntervalInSeconds = (int)AppConfigSettings.PollInterval.TotalSeconds,
                }, stoppingToken)).InitialConfigurationToken;

                var response = await client.GetLatestConfigurationAsync(new GetLatestConfigurationRequest { ConfigurationToken = token }, stoppingToken);
                token = response.NextPollConfigurationToken;
                if (response.NextPollIntervalInSeconds is > 0 and var seconds)
                {
                    wait = TimeSpan.FromSeconds(Math.Max(seconds, AppConfigSettings.PollInterval.TotalSeconds));
                }

                if (response.Configuration is { Length: > 0 } content)
                {
                    using var reader = new StreamReader(content);
                    var parsed = AppConfigFeatureFlags.Parse(await reader.ReadToEndAsync(stoppingToken));
                    flags.Update(parsed);
                    var summary = string.Join(", ", parsed.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={(f.Value.Enabled ? "on" : "off")}"));
                    LogFlagsUpdated(summary);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Any failure keeps the last flags and retries with a fresh session.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPollFailed(ex, settings.Application, settings.Environment, settings.Profile);
                token = null;
            }

            try
            {
                await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 100, EventName = "FeatureFlagsUpdated", Level = LogLevel.Information, Message = "Feature flags updated from AppConfig: {Flags}")]
    private partial void LogFlagsUpdated(string flags);

    [LoggerMessage(EventId = 101, EventName = "FeatureFlagsPollFailed", Level = LogLevel.Warning,
        Message = "Could not read feature flags from AppConfig {Application}/{Environment}/{Profile}; keeping the last ones")]
    private partial void LogPollFailed(Exception exception, string application, string environment, string profile);
}
