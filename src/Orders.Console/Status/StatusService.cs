using System.Collections.Concurrent;
using Orders.Console.Domain;
using Orders.Console.Events;
using Orders.Console.Integrations;

namespace Orders.Console.Status;

/// <summary>A panel's value, or why it could not be read.</summary>
public sealed record Panel<T>(T? Value, string? Error, DateTimeOffset At)
{
    public bool Ok => Error is null && Value is not null;
}

public sealed record StatusSnapshot(
    Panel<EnvironmentHealth> Prod,
    Panel<EnvironmentHealth> Staging,
    Panel<IReadOnlyList<AlarmState>> Alarms,
    Panel<IReadOnlyList<LogLine>> Errors,
    Panel<IReadOnlyList<EnvironmentTraffic>> Traffic,
    Panel<GitHubActivity> GitHub,
    Panel<IReadOnlyList<DeployRecord>> Deploys,
    Panel<CostSummary> Cost,
    Panel<IReadOnlyList<FeatureFlag>> Flags,
    Panel<LoadStatus> Load,
    IReadOnlyList<ConsoleEvent> RecentEvents,
    IReadOnlyList<SourceState> Sources);

/// <summary>
/// Reads every Status panel in parallel. In real mode each panel is cached for a short time so a
/// room full of browsers polling every few seconds does not hit GitHub and AWS limits (Cost Explorer
/// charges per request, so cost is cached for an hour).
/// </summary>
public sealed class StatusService(
    IEnvironmentProbe probe,
    IMonitoringService monitoring,
    IGitHubService github,
    IDeploymentService deployments,
    ICostService cost,
    IFeatureFlagService flags,
    ILoadGenerator load,
    EventStore events,
    SourceHealth sources,
    ConsoleOptions options,
    TimeProvider time)
{
    public const string ProdPanel = "prod";
    public const string StagingPanel = "staging";
    public const string AlarmsPanel = "alarms";
    public const string ErrorsPanel = "errors";
    public const string TrafficPanel = "traffic";
    public const string GitHubPanel = "github";
    public const string DeploysPanel = "deploys";
    public const string CostPanel = "cost";
    public const string FlagsPanel = "flags";
    public const string LoadPanel = "load";

    private static readonly Dictionary<string, TimeSpan> Ttl = new()
    {
        [ProdPanel] = TimeSpan.FromSeconds(10),
        [StagingPanel] = TimeSpan.FromSeconds(10),
        [AlarmsPanel] = TimeSpan.FromSeconds(10),
        [ErrorsPanel] = TimeSpan.FromSeconds(20),
        [TrafficPanel] = TimeSpan.FromSeconds(30),
        [GitHubPanel] = TimeSpan.FromSeconds(15),
        [DeploysPanel] = TimeSpan.FromSeconds(15),
        [CostPanel] = TimeSpan.FromHours(1),
        [FlagsPanel] = TimeSpan.FromSeconds(10),
        [LoadPanel] = TimeSpan.FromSeconds(5),
    };

    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, object Panel)> _cache = new();

    public void Invalidate(string panel) => _cache.TryRemove(panel, out _);

    public async Task<StatusSnapshot> GetAsync(CancellationToken ct)
    {
        var prod = Read(ProdPanel, c => probe.CheckAsync("prod", c), ct);
        var staging = Read(StagingPanel, c => probe.CheckAsync("staging", c), ct);
        var alarms = Read(AlarmsPanel, monitoring.GetAlarmsAsync, ct);
        var errors = Read(ErrorsPanel, monitoring.GetRecentErrorsAsync, ct);
        var traffic = Read(TrafficPanel, monitoring.GetTrafficAsync, ct);
        var gh = Read(GitHubPanel, github.GetActivityAsync, ct);
        var deploys = Read(DeploysPanel, deployments.GetRecentDeploysAsync, ct);
        var costs = Read(CostPanel, cost.GetCostAsync, ct);
        var flagList = Read(FlagsPanel, flags.GetFlagsAsync, ct);
        var loadStatus = Read(LoadPanel, load.GetStatusAsync, ct);
        await Task.WhenAll(prod, staging, alarms, errors, traffic, gh, deploys, costs, flagList, loadStatus);
        return new StatusSnapshot(
            prod.Result, staging.Result, alarms.Result, errors.Result, traffic.Result, gh.Result, deploys.Result,
            costs.Result, flagList.Result, loadStatus.Result, events.Latest(15), sources.All());
    }

    /// <summary>Production and staging traffic, cached like the Status panel (the Tour's live-traffic step shows it too).</summary>
    public Task<Panel<IReadOnlyList<EnvironmentTraffic>>> GetTrafficAsync(CancellationToken ct) => Read(TrafficPanel, monitoring.GetTrafficAsync, ct);

    private async Task<Panel<T>> Read<T>(string name, Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!options.Fakes && _cache.TryGetValue(name, out var hit) && hit.Until > now && hit.Panel is Panel<T> cached)
        {
            return cached;
        }

        Panel<T> panel;
        try
        {
            panel = new Panel<T>(await read(ct), null, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            panel = new Panel<T>(default, ex.Message, now);
        }

        // Errors are cached briefly too, so a broken dependency is not hammered.
        var ttl = panel.Error is null ? Ttl[name] : TimeSpan.FromSeconds(Math.Min(30, Ttl[name].TotalSeconds));
        _cache[name] = (now + ttl, panel);
        return panel;
    }
}
