using System.Globalization;

namespace Orders.Api.Infrastructure;

/// <summary>A flag's state: whether it is on, plus its attributes (for example <c>percent</c>).</summary>
public sealed record FeatureFlag(bool Enabled, IReadOnlyDictionary<string, string> Attributes)
{
    public static readonly FeatureFlag Off = new(false, new Dictionary<string, string>());

    /// <summary>A numeric attribute, or <paramref name="fallback"/> if it is missing or not a number.</summary>
    public double Number(string attribute, double fallback) =>
        Attributes.TryGetValue(attribute, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

/// <summary>
/// Feature flags. In AWS they come from AppConfig (<see cref="AppConfigFeatureFlags"/>, used when APPCONFIG_APPLICATION
/// is set); otherwise from configuration (<see cref="ConfigurationFeatureFlags"/>).
/// </summary>
public interface IFeatureFlags
{
    /// <summary>The flag's current state; <see cref="FeatureFlag.Off"/> for a flag nobody has defined.</summary>
    FeatureFlag GetFlag(string flag);
}

public static class FeatureFlagExtensions
{
    public static bool IsEnabled(this IFeatureFlags flags, string flag) => flags.GetFlag(flag).Enabled;
}

/// <summary>Flag names shared with the AppConfig profile in infra/terraform/base/appconfig.tf.</summary>
public static class Flags
{
    public const string SearchV2 = "searchV2";
    public const string FaultSlowSearch = "faultSlowSearch";
    public const string FaultFailOrders = "faultFailOrders";
}

/// <summary>
/// Flags from configuration: <c>FeatureFlags__searchV2=true</c>, or <c>FeatureFlags__faultFailOrders__enabled=true</c>
/// with attributes alongside, e.g. <c>FeatureFlags__faultFailOrders__percent=50</c>.
/// </summary>
internal sealed class ConfigurationFeatureFlags(IConfiguration configuration) : IFeatureFlags
{
    public FeatureFlag GetFlag(string flag)
    {
        var section = configuration.GetSection($"FeatureFlags:{flag}");
        var enabled = section.Value is not null ? section.Get<bool>() : section.GetValue<bool>("enabled");
        var attributes = section.GetChildren()
            .Where(c => c.Value is not null && !string.Equals(c.Key, "enabled", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.Ordinal);
        return new FeatureFlag(enabled, attributes);
    }
}
