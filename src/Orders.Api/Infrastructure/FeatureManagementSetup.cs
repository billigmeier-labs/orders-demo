using System.Globalization;
using Microsoft.FeatureManagement;

namespace Orders.Api.Infrastructure;

/// <summary>
/// Feeds Microsoft.FeatureManagement from <see cref="IFeatureFlags"/> (AWS AppConfig in ECS, configuration
/// locally), so the app asks <see cref="IFeatureManager"/> whether a feature is on. Every call reads the current
/// flag, so a change in AppConfig applies on the next request after the poller sees it.
/// </summary>
/// <remarks>
/// A flag that is off has no filters (always off). A flag that is on becomes <c>AlwaysOn</c>, unless it has a
/// <c>percent</c> attribute or is a fault flag: then it is on for that share of calls through <see cref="PercentFilter"/>
/// (fault flags without a percent use <see cref="FaultInjector.DefaultPercent"/>).
/// </remarks>
internal sealed class FlagFeatureDefinitionProvider(IFeatureFlags flags) : IFeatureDefinitionProvider
{
    private static readonly FeatureFilterConfiguration[] AlwaysOn = [new() { Name = "AlwaysOn" }];

    public Task<FeatureDefinition> GetFeatureDefinitionAsync(string featureName) =>
        Task.FromResult(Define(featureName, flags.GetFlag(featureName)));

    public async IAsyncEnumerable<FeatureDefinition> GetAllFeatureDefinitionsAsync()
    {
        foreach (var name in Flags.All)
        {
            yield return await GetFeatureDefinitionAsync(name);
        }
    }

    internal static FeatureDefinition Define(string name, FeatureFlag flag)
    {
        if (!flag.Enabled)
        {
            return new FeatureDefinition { Name = name, EnabledFor = [] };
        }

        var percent = Percent(name, flag);
        return new FeatureDefinition
        {
            Name = name,
            EnabledFor = percent is { } value ? [PercentFilter.Configuration(value)] : AlwaysOn,
        };
    }

    /// <summary>The share of calls a flag is on for (0–100), or null when it is simply on.</summary>
    internal static double? Percent(string name, FeatureFlag flag)
    {
        var configured = flag.Attributes.ContainsKey(PercentFilter.Attribute);
        if (!configured && !Flags.Faults.Contains(name))
        {
            return null;
        }

        return Math.Clamp(flag.Number(PercentFilter.Attribute, FaultInjector.DefaultPercent), 0, 100);
    }
}

/// <summary>On for <c>Value</c>% of evaluations, decided independently each time (like Microsoft.Percentage, but testable).</summary>
[FilterAlias(Alias)]
internal sealed class PercentFilter(Func<double> roll) : IFeatureFilter
{
    public const string Alias = "Percent";
    public const string Attribute = "percent";

    public PercentFilter()
#pragma warning disable CA5394 // Not used for security.
        : this(() => Random.Shared.NextDouble() * 100)
#pragma warning restore CA5394
    {
    }

    public static FeatureFilterConfiguration Configuration(double percent) => new()
    {
        Name = Alias,
        Parameters = new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create<string, string?>("Value", percent.ToString(CultureInfo.InvariantCulture))])
            .Build(),
    };

    public Task<bool> EvaluateAsync(FeatureFilterEvaluationContext context)
    {
        var percent = context.Parameters.GetValue<double>("Value");
        return Task.FromResult(percent > 0 && roll() < percent);
    }
}

internal static class FeatureManagementSetup
{
    /// <summary>Microsoft.FeatureManagement over the app's <see cref="IFeatureFlags"/>.</summary>
    public static IServiceCollection AddAppFeatureManagement(this IServiceCollection services)
    {
        // Registered before AddFeatureManagement, which only adds its configuration-based provider if none is set.
        services.AddSingleton<IFeatureDefinitionProvider, FlagFeatureDefinitionProvider>();
        services.AddFeatureManagement().AddFeatureFilter<PercentFilter>();
        return services;
    }
}
