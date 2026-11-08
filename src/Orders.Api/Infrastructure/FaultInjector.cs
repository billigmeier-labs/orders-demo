namespace Orders.Api.Infrastructure;

/// <summary>Thrown on purpose by <see cref="FaultInjector"/>; the exception handler turns it into a 500.</summary>
public sealed class InjectedFaultException(string flag) : Exception($"Injected fault: {flag} is on.")
{
    public string Flag { get; } = flag;
}

/// <summary>
/// Demo faults driven by feature flags with a <c>percent</c> attribute: <see cref="Flags.FaultFailOrders"/>
/// fails order creates, <see cref="Flags.FaultSlowSearch"/> slows searches. Every fault that fires logs a
/// <c>FaultInjected</c> warning with the flag and percent, so it is obvious in the logs what was on purpose.
/// </summary>
internal sealed partial class FaultInjector(IFeatureFlags flags, ILogger<FaultInjector> logger, Func<double> roll)
{
    /// <summary>Percent used when a fault flag is on but has no percent attribute.</summary>
    public const double DefaultPercent = 20;

    public static readonly TimeSpan SlowSearchDelay = TimeSpan.FromSeconds(2);

    public FaultInjector(IFeatureFlags flags, ILogger<FaultInjector> logger)
#pragma warning disable CA5394 // Not used for security.
        : this(flags, logger, () => Random.Shared.NextDouble() * 100)
#pragma warning restore CA5394
    {
    }

    /// <summary>Throws <see cref="InjectedFaultException"/> for <c>percent</c>% of calls while faultFailOrders is on.</summary>
    public void MaybeFailOrder()
    {
        if (ShouldFire(Flags.FaultFailOrders, out var percent))
        {
            LogFaultInjected(Flags.FaultFailOrders, percent, "fail order create with 500");
            throw new InjectedFaultException(Flags.FaultFailOrders);
        }
    }

    /// <summary>Waits <see cref="SlowSearchDelay"/> for <c>percent</c>% of calls while faultSlowSearch is on.</summary>
    public Task MaybeSlowSearchAsync(CancellationToken ct)
    {
        if (!ShouldFire(Flags.FaultSlowSearch, out var percent))
        {
            return Task.CompletedTask;
        }

        LogFaultInjected(Flags.FaultSlowSearch, percent, "delay search by 2 s");
        return Task.Delay(SlowSearchDelay, ct);
    }

    internal bool ShouldFire(string flag, out double percent)
    {
        var state = flags.GetFlag(flag);
        percent = Math.Clamp(state.Number("percent", DefaultPercent), 0, 100);
        return state.Enabled && percent > 0 && roll() < percent;
    }

    [LoggerMessage(EventId = 200, EventName = "FaultInjected", Level = LogLevel.Warning,
        Message = "FaultInjected: {Flag} at {Percent}% fired ({Effect})")]
    private partial void LogFaultInjected(string flag, double percent, string effect);
}
