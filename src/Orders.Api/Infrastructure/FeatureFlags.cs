namespace Orders.Api.Infrastructure;

/// <summary>
/// Feature flags. Today they come from configuration (<c>FeatureFlags__&lt;name&gt;=true</c>).
/// In AWS they will come from AppConfig (APPCONFIG_APPLICATION, APPCONFIG_ENVIRONMENT,
/// APPCONFIG_PROFILE=feature-flags): add another implementation and register it instead.
/// </summary>
public interface IFeatureFlags
{
    bool IsEnabled(string flag);
}

internal sealed class ConfigurationFeatureFlags(IConfiguration configuration) : IFeatureFlags
{
    public bool IsEnabled(string flag) => configuration.GetValue<bool>($"FeatureFlags:{flag}");
}
