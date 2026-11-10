using Microsoft.FeatureManagement;

namespace Orders.Api.Infrastructure;

/// <summary>Thrown on purpose by <see cref="FaultInjector"/>; the exception handler turns it into a 500.</summary>
public sealed class InjectedFaultException(string flag) : Exception($"Injected fault: {flag} is on.")
{
    public string Flag { get; } = flag;
}

/// <summary>
/// Demo faults driven by feature flags with a <c>percent</c> attribute: <see cref="Flags.FaultFailOrders"/>
/// fails order creates, <see cref="Flags.FaultSlowSearch"/> slows searches. Whether a fault fires is up to
/// Microsoft.FeatureManagement (<see cref="PercentFilter"/> turns the percent into a share of calls). Every
/// fault that fires logs a <c>FaultInjected</c> warning with the flag and percent, so it is obvious in the
/// logs what was on purpose.
/// </summary>
internal sealed partial class FaultInjector(IFeatureManager features, IFeatureFlags flags, ILogger<FaultInjector> logger)
{
    /// <summary>Percent used when a fault flag is on but has no percent attribute.</summary>
    public const double DefaultPercent = 20;

    public static readonly TimeSpan SlowSearchDelay = TimeSpan.FromSeconds(2);

    /// <summary>Throws <see cref="InjectedFaultException"/> for <c>percent</c>% of calls while faultFailOrders is on.</summary>
    public async Task MaybeFailOrderAsync()
    {
        if (await features.IsEnabledAsync(Flags.FaultFailOrders))
        {
            LogFaultInjected(Flags.FaultFailOrders, PercentOf(Flags.FaultFailOrders), "fail order create with 500");
            throw new InjectedFaultException(Flags.FaultFailOrders);
        }
    }

    /// <summary>Waits <see cref="SlowSearchDelay"/> for <c>percent</c>% of calls while faultSlowSearch is on.</summary>
    public async Task MaybeSlowSearchAsync(CancellationToken ct)
    {
        if (await features.IsEnabledAsync(Flags.FaultSlowSearch))
        {
            LogFaultInjected(Flags.FaultSlowSearch, PercentOf(Flags.FaultSlowSearch), "delay search by 2 s");
            await Task.Delay(SlowSearchDelay, ct);
        }
    }

    private double PercentOf(string flag) => FlagFeatureDefinitionProvider.Percent(flag, flags.GetFlag(flag)) ?? 100;

    [LoggerMessage(EventId = 200, EventName = "FaultInjected", Level = LogLevel.Warning,
        Message = "FaultInjected: {Flag} at {Percent}% fired ({Effect})")]
    private partial void LogFaultInjected(string flag, double percent, string effect);
}
