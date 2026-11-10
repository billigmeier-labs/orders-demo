using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

public class FeatureManagementTests
{
    private readonly FakeFeatureFlags _flags = new();

    [Fact]
    public async Task SearchV2_FollowsTheFlagWithNoPercent()
    {
        var features = Features.Manager(_flags, roll: 99.9);
        Assert.False(await features.IsEnabledAsync(Flags.SearchV2, TestContext.Current.CancellationToken));

        _flags.Set(Flags.SearchV2, true);
        Assert.True(await features.IsEnabledAsync(Flags.SearchV2, TestContext.Current.CancellationToken));

        _flags.Set(Flags.SearchV2, false);
        Assert.False(await features.IsEnabledAsync(Flags.SearchV2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownFeature_IsOff() =>
        Assert.False(await Features.Manager(_flags).IsEnabledAsync("noSuchFlag", TestContext.Current.CancellationToken));

    [Theory]
    [InlineData(40, 39.9, true)]
    [InlineData(40, 40, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, -1, false)]
    public async Task PercentAttribute_TurnsOnForThatShare(double percent, double roll, bool on)
    {
        _flags.Set("newCheckout", true, percent);

        Assert.Equal(on, await Features.Manager(_flags, roll).IsEnabledAsync("newCheckout", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DefaultPercentFilter_RollsAcrossTheWholeRange()
    {
        var filter = new PercentFilter();
        var half = PercentFilter.Configuration(50);
        var context = new FeatureFilterEvaluationContext { FeatureName = "f", Parameters = half.Parameters! };

        // A roll in [0, 100) is on for roughly half the calls at 50%. A roll scaled to [0, 1) would make every call on.
        var on = 0;
        const int runs = 400;
        for (var i = 0; i < runs; i++)
        {
            if (await filter.EvaluateAsync(context))
            {
                on++;
            }
        }

        Assert.InRange(on, 1, runs - 1);
    }

    [Fact]
    public void Define_OffHasNoFilters_OnIsAlwaysOn_FaultsArePercent()
    {
        Assert.Empty(FlagFeatureDefinitionProvider.Define(Flags.SearchV2, FeatureFlag.Off).EnabledFor);
        _flags.Set(Flags.SearchV2, true).Set(Flags.FaultSlowSearch, true);

        var always = Assert.Single(FlagFeatureDefinitionProvider.Define(Flags.SearchV2, _flags.GetFlag(Flags.SearchV2)).EnabledFor);
        Assert.Equal("AlwaysOn", always.Name);
        var fault = Assert.Single(FlagFeatureDefinitionProvider.Define(Flags.FaultSlowSearch, _flags.GetFlag(Flags.FaultSlowSearch)).EnabledFor);
        Assert.Equal(PercentFilter.Alias, fault.Name);
        Assert.Equal(FaultInjector.DefaultPercent, fault.Parameters.GetValue<double>("Value"));
    }

    [Fact]
    public async Task AllDefinitions_AreTheKnownFlags()
    {
        var names = new List<string>();
        await foreach (var definition in new FlagFeatureDefinitionProvider(_flags).GetAllFeatureDefinitionsAsync())
        {
            names.Add(definition.Name);
        }

        Assert.Equal(Flags.All, names);
    }

    [Fact]
    public async Task Registration_UsesTheFlagsNotConfiguration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create<string, string?>("FeatureManagement:searchV2", "true")]).Build());
        services.AddLogging();
        services.AddSingleton<IFeatureFlags>(_flags);
        services.AddAppFeatureManagement();
        await using var provider = services.BuildServiceProvider();

        var features = provider.GetRequiredService<IFeatureManager>();

        Assert.IsType<FlagFeatureDefinitionProvider>(provider.GetRequiredService<IFeatureDefinitionProvider>());
        Assert.False(await features.IsEnabledAsync(Flags.SearchV2));
        _flags.Set(Flags.SearchV2, true);
        Assert.True(await features.IsEnabledAsync(Flags.SearchV2));
    }
}
