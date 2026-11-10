using Microsoft.Extensions.Configuration;
using Orders.Api.Features.Platform;

namespace Orders.UnitTests;

public class AppInfoTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => KeyValuePair.Create<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void From_WithoutSettings_UsesLocalDefaults()
    {
        var info = AppInfo.From(Config());

        Assert.Equal("0.0.0-local", info.Version);
        Assert.Equal("unknown", info.Commit);
        Assert.Equal("local", info.Environment);
    }

    [Fact]
    public void From_WithSettings_UsesThem()
    {
        var info = AppInfo.From(Config(("APP_VERSION", "1.2.3"), ("APP_COMMIT", "abc1234"), ("APP_ENVIRONMENT", "staging")));

        Assert.Equal(new AppInfo("1.2.3", "abc1234", "staging"), info);
    }
}
