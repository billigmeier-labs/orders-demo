using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

public class AppConfigFeatureFlagTests
{
    [Fact]
    public void Parse_DataPlaneFormat_ReadsEnabledAndAttributes()
    {
        const string json = """
            {
              "searchV2": { "enabled": true },
              "faultSlowSearch": { "enabled": false, "percent": 20 },
              "faultFailOrders": { "enabled": true, "percent": 35.5, "note": "demo", "strict": true }
            }
            """;

        var flags = AppConfigFeatureFlags.Parse(json);

        Assert.True(flags["searchV2"].Enabled);
        Assert.Empty(flags["searchV2"].Attributes);
        Assert.False(flags["faultSlowSearch"].Enabled);
        Assert.Equal(20, flags["faultSlowSearch"].Number("percent", 0));
        Assert.True(flags["faultFailOrders"].Enabled);
        Assert.Equal(35.5, flags["faultFailOrders"].Number("percent", 0));
        Assert.Equal("demo", flags["faultFailOrders"].Attributes["note"]);
        Assert.Equal("true", flags["faultFailOrders"].Attributes["strict"]);
    }

    [Fact]
    public void Parse_HostedDocumentFormat_ReadsTheValues()
    {
        const string json = """
            {
              "version": "1",
              "flags": { "faultFailOrders": { "name": "faultFailOrders", "attributes": { "percent": { "constraints": { "type": "number" } } } } },
              "values": { "faultFailOrders": { "enabled": true, "percent": 50 } }
            }
            """;

        var flag = AppConfigFeatureFlags.Parse(json)["faultFailOrders"];

        Assert.True(flag.Enabled);
        Assert.Equal(50, flag.Number("percent", 0));
    }

    [Fact]
    public void Parse_MissingEnabledOrNonObjectEntries_AreOffOrSkipped()
    {
        var flags = AppConfigFeatureFlags.Parse("""{ "a": {}, "b": { "enabled": "yes" }, "c": 1, "d": { "enabled": true, "list": [1, 2] } }""");

        Assert.False(flags["a"].Enabled);
        Assert.False(flags["b"].Enabled);
        Assert.False(flags.ContainsKey("c"));
        Assert.True(flags["d"].Enabled);
        Assert.Empty(flags["d"].Attributes);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Parse_NotAnObject_Throws(string json) =>
        Assert.ThrowsAny<JsonException>(() => AppConfigFeatureFlags.Parse(json));

    [Fact]
    public void Parse_NotAnObject_SaysWhy()
    {
        var ex = Assert.Throws<JsonException>(() => AppConfigFeatureFlags.Parse("[]"));

        Assert.Equal("Feature flag content must be a JSON object.", ex.Message);
    }

    [Fact]
    public void Parse_FlagsWithoutValues_TreatsTheRootAsTheFlags()
    {
        var flags = AppConfigFeatureFlags.Parse("""{ "flags": { "enabled": true }, "other": { "enabled": true } }""");

        Assert.True(flags["flags"].Enabled);
        Assert.True(flags["other"].Enabled);
    }

    [Fact]
    public void Parse_ValuesWithoutFlags_TreatsTheRootAsTheFlags()
    {
        var flags = AppConfigFeatureFlags.Parse("""{ "values": { "enabled": true }, "other": { "enabled": true } }""");

        Assert.True(flags["values"].Enabled);
        Assert.True(flags["other"].Enabled);
    }

    [Fact]
    public void Parse_FlagsThatIsNotAnObject_TreatsTheRootAsTheFlags()
    {
        var flags = AppConfigFeatureFlags.Parse("""{ "flags": 1, "values": { "enabled": true } }""");

        Assert.Single(flags);
        Assert.True(flags["values"].Enabled);
    }

    [Fact]
    public void GetFlag_BeforeFirstPollOrUnknown_IsOff()
    {
        var flags = new AppConfigFeatureFlags();

        Assert.Same(FeatureFlag.Off, flags.GetFlag("searchV2"));

        flags.Update(AppConfigFeatureFlags.Parse("""{ "searchV2": { "enabled": true } }"""));

        Assert.True(flags.IsEnabled("searchV2"));
        Assert.False(flags.IsEnabled("faultFailOrders"));
    }

    [Fact]
    public void Number_MissingOrNotANumber_UsesFallback()
    {
        var flag = new FeatureFlag(true, new Dictionary<string, string> { ["percent"] = "lots" });

        Assert.Equal(20, flag.Number("percent", 20));
        Assert.Equal(7, flag.Number("other", 7));
    }

    [Fact]
    public void Settings_WithoutApplication_IsNull() =>
        Assert.Null(AppConfigSettings.From(new ConfigurationBuilder().Build()));

    [Fact]
    public void Settings_OnlyApplication_UsesDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["APPCONFIG_APPLICATION"] = "orders-demo" })
            .Build();

        Assert.Equal(new AppConfigSettings("orders-demo", "prod", "feature-flags"), AppConfigSettings.From(configuration));
    }

    [Fact]
    public void Settings_FromEnvironmentNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APPCONFIG_APPLICATION"] = "orders-demo",
                ["APPCONFIG_ENVIRONMENT"] = "staging",
                ["APPCONFIG_PROFILE"] = "custom-flags",
            })
            .Build();

        Assert.Equal(new AppConfigSettings("orders-demo", "staging", "custom-flags"), AppConfigSettings.From(configuration));
    }
}
