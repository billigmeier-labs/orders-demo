using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

public class FaultInjectorTests
{
    private readonly FakeFeatureFlags _flags = new();
    private readonly ListLogger<FaultInjector> _logger = new();

    private FaultInjector Faults(double roll) => new(Features.Manager(_flags, roll), _flags, _logger);

    [Fact]
    public async Task MaybeFailOrder_FlagOff_DoesNothing()
    {
        _flags.Set(Flags.FaultFailOrders, false, percent: 100);

        await Faults(0).MaybeFailOrderAsync();

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task MaybeFailOrder_FlagOnAndRollUnderPercent_ThrowsAndLogsFaultInjected()
    {
        _flags.Set(Flags.FaultFailOrders, true, percent: 30);

        var ex = await Assert.ThrowsAsync<InjectedFaultException>(() => Faults(29.9).MaybeFailOrderAsync());

        Assert.Equal(Flags.FaultFailOrders, ex.Flag);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("FaultInjected", entry.EventId.Name);
        Assert.Contains(entry.State, kv => kv.Key == "Flag" && (string?)kv.Value == Flags.FaultFailOrders);
        Assert.Contains(entry.State, kv => kv.Key == "Percent" && (double?)kv.Value == 30);
    }

    [Fact]
    public async Task MaybeFailOrder_RollAtOrAbovePercent_DoesNotFire()
    {
        _flags.Set(Flags.FaultFailOrders, true, percent: 30);

        await Faults(30).MaybeFailOrderAsync();

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task MaybeFailOrder_NoPercentAttribute_UsesDefault()
    {
        _flags.Set(Flags.FaultFailOrders, true);

        await Assert.ThrowsAsync<InjectedFaultException>(() => Faults(FaultInjector.DefaultPercent - 0.1).MaybeFailOrderAsync());
        Assert.Contains(Assert.Single(_logger.Entries).State, kv => kv.Key == "Percent" && (double?)kv.Value == FaultInjector.DefaultPercent);
        await Faults(FaultInjector.DefaultPercent).MaybeFailOrderAsync();
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(-5, 0, false)]
    [InlineData(100, 99.99, true)]
    [InlineData(250, 99.99, true)]
    public async Task MaybeSlowSearch_PercentIsClamped(double configured, double roll, bool fires)
    {
        _flags.Set(Flags.FaultSlowSearch, true, percent: configured);

        // Already cancelled, so a delay that fires ends at once with a cancellation.
        var task = Faults(roll).MaybeSlowSearchAsync(new CancellationToken(canceled: true));
        if (fires)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        else
        {
            await task;
        }

        Assert.Equal(fires ? 1 : 0, _logger.Entries.Count);
    }

    [Fact]
    public void MaybeSlowSearch_Fires_DelaysAndLogs()
    {
        _flags.Set(Flags.FaultSlowSearch, true, percent: 100);

        var task = Faults(50).MaybeSlowSearchAsync(TestContext.Current.CancellationToken);

        Assert.False(task.IsCompleted);
        Assert.Equal("FaultInjected", Assert.Single(_logger.Entries).EventId.Name);
    }

    [Fact]
    public async Task MaybeSlowSearch_FlagOff_CompletesImmediately()
    {
        var task = Faults(0).MaybeSlowSearchAsync(TestContext.Current.CancellationToken);

        Assert.True(task.IsCompleted);
        await task;
        Assert.Empty(_logger.Entries);
    }
}

/// <summary>A real FeatureManager over test flags, with a fixed roll for percent filters.</summary>
internal static class Features
{
    public static FeatureManager Manager(IFeatureFlags flags, double roll = 0) =>
        new(new FlagFeatureDefinitionProvider(flags))
        {
            FeatureFilters = [new PercentFilter(() => roll)],
        };
}
