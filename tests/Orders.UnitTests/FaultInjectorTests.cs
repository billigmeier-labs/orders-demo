using Microsoft.Extensions.Logging;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

public class FaultInjectorTests
{
    private readonly FakeFeatureFlags _flags = new();
    private readonly ListLogger<FaultInjector> _logger = new();

    private FaultInjector Faults(double roll) => new(_flags, _logger, () => roll);

    [Fact]
    public void MaybeFailOrder_FlagOff_DoesNothing()
    {
        _flags.Set(Flags.FaultFailOrders, false, percent: 100);

        Faults(0).MaybeFailOrder();

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void MaybeFailOrder_FlagOnAndRollUnderPercent_ThrowsAndLogsFaultInjected()
    {
        _flags.Set(Flags.FaultFailOrders, true, percent: 30);

        var ex = Assert.Throws<InjectedFaultException>(() => Faults(29.9).MaybeFailOrder());

        Assert.Equal(Flags.FaultFailOrders, ex.Flag);
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("FaultInjected", entry.EventId.Name);
        Assert.Contains(entry.State, kv => kv.Key == "Flag" && (string?)kv.Value == Flags.FaultFailOrders);
        Assert.Contains(entry.State, kv => kv.Key == "Percent" && (double?)kv.Value == 30);
    }

    [Fact]
    public void MaybeFailOrder_RollAtOrAbovePercent_DoesNotFire()
    {
        _flags.Set(Flags.FaultFailOrders, true, percent: 30);

        Faults(30).MaybeFailOrder();

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void ShouldFire_NoPercentAttribute_UsesDefault()
    {
        _flags.Set(Flags.FaultFailOrders, true);

        Assert.True(Faults(FaultInjector.DefaultPercent - 0.1).ShouldFire(Flags.FaultFailOrders, out var percent));
        Assert.Equal(FaultInjector.DefaultPercent, percent);
        Assert.False(Faults(FaultInjector.DefaultPercent).ShouldFire(Flags.FaultFailOrders, out _));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(-5, 0, false)]
    [InlineData(100, 99.99, true)]
    [InlineData(250, 99.99, true)]
    public void ShouldFire_PercentIsClamped(double configured, double roll, bool fires)
    {
        _flags.Set(Flags.FaultSlowSearch, true, percent: configured);

        Assert.Equal(fires, Faults(roll).ShouldFire(Flags.FaultSlowSearch, out _));
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
    public void MaybeSlowSearch_FlagOff_CompletesImmediately()
    {
        Assert.True(Faults(0).MaybeSlowSearchAsync(TestContext.Current.CancellationToken).IsCompleted);
        Assert.Empty(_logger.Entries);
    }
}
