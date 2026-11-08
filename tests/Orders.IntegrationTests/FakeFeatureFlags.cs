using System.Globalization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Api.Infrastructure;

namespace Orders.IntegrationTests;

/// <summary>Flags a test sets directly; <see cref="WithFlags"/> puts them into an API.</summary>
internal sealed class FakeFeatureFlags : IFeatureFlags
{
    private readonly Dictionary<string, FeatureFlag> _flags = new(StringComparer.Ordinal);

    public FakeFeatureFlags Set(string flag, bool enabled, double? percent = null)
    {
        _flags[flag] = new FeatureFlag(
            enabled,
            percent is null ? [] : new Dictionary<string, string> { ["percent"] = percent.Value.ToString(CultureInfo.InvariantCulture) });
        return this;
    }

    public FeatureFlag GetFlag(string flag) => _flags.GetValueOrDefault(flag) ?? FeatureFlag.Off;
}

internal static class FeatureFlagFactoryExtensions
{
    /// <summary>The same API and database, with these flags instead of the configured ones.</summary>
    public static WebApplicationFactory<Program> WithFlags(this OrdersApiFactory factory, FakeFeatureFlags flags) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFeatureFlags>();
            services.AddSingleton<IFeatureFlags>(flags);
        }));
}
