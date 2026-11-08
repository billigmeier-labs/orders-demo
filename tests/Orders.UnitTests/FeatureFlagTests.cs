using Microsoft.Extensions.Configuration;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

public class FeatureFlagTests
{
    private static ConfigurationFeatureFlags Flags(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => KeyValuePair.Create<string, string?>(s.Key, s.Value)))
            .Build());

    [Fact]
    public void IsEnabled_FlagSetTrue_ReturnsTrue() =>
        Assert.True(Flags(("FeatureFlags:NewCheckout", "true")).IsEnabled("NewCheckout"));

    [Fact]
    public void IsEnabled_FlagSetFalse_ReturnsFalse() =>
        Assert.False(Flags(("FeatureFlags:NewCheckout", "false")).IsEnabled("NewCheckout"));

    [Fact]
    public void IsEnabled_FlagMissing_ReturnsFalse() =>
        Assert.False(Flags().IsEnabled("NewCheckout"));
}
