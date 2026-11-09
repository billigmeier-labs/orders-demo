using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Orders.Console.Integrations;
using Orders.Console.Integrations.Aws;
using Orders.Console.Integrations.Fakes;

namespace Orders.Console.Tests;

public sealed class FeatureFlagDescriptionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Flag_descriptions_come_from_appconfig_then_the_built_in_list_never_the_name()
    {
        var doc = JsonNode.Parse("""
            {
              "version": "1",
              "flags": {
                "searchV2": { "name": "searchV2" },
                "faultSlowSearch": { "name": "faultSlowSearch", "_description": "From AppConfig" },
                "faultFailOrders": { "name": "faultFailOrders", "description": "  " },
                "newThing": { "name": "newThing" },
                "darkMode": { "name": "Dark mode" }
              },
              "values": { "searchV2": { "enabled": true } }
            }
            """);
        var flags = AwsFeatureFlags.Parse(doc).ToDictionary(f => f.Name);

        Assert.Equal(KnownFlags.Describe("searchV2"), flags["searchV2"].Description);
        Assert.True(flags["searchV2"].Enabled);
        Assert.Equal("From AppConfig", flags["faultSlowSearch"].Description);
        Assert.Equal("Fault: make some order requests fail", flags["faultFailOrders"].Description);
        Assert.Null(flags["newThing"].Description);
        Assert.Equal("Dark mode", flags["darkMode"].Description);
    }

    [Fact]
    public async Task Fake_flags_use_the_same_descriptions_as_the_real_ones()
    {
        var world = new FakeWorld(new FakeTimeProvider(), new ConsoleOptions());
        var flags = (await world.GetFlagsAsync(Ct)).ToDictionary(f => f.Name);
        foreach (var (name, description) in KnownFlags.Descriptions)
        {
            Assert.Equal(description, flags[name].Description);
        }

        Assert.All(flags.Values, f => Assert.NotEqual(f.Name, f.Description));
    }
}
