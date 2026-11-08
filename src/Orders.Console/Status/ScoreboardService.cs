using System.Globalization;
using Orders.Console.Domain;
using Orders.Console.Events;
using Orders.Console.Integrations;

namespace Orders.Console.Status;

public sealed record ScoreRow(string Id, string Name, string Value, string Hint);

public sealed record Scoreboard(IReadOnlyList<ScoreRow> Rows, DateTimeOffset At);

/// <summary>Delivery metrics for the scoreboard scenario.</summary>
public sealed class ScoreboardService(IGitHubService github, ICostService cost, EventStore events, TimeProvider time)
{
    public async Task<Scoreboard> GetAsync(CancellationToken ct)
    {
        var stats = await github.GetDeliveryStatsAsync(ct);
        CostSummary? spend = null;
        try
        {
            spend = await cost.GetCostAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cost is optional on the scoreboard.
        }

        var since = time.GetUtcNow().AddDays(-30);
        var rollbacks = Math.Max(
            events.Count(EventKinds.DeployRolledBack, since),
            stats.DeployRuns.Count(r => r.Conclusion is "failure"));

        var rows = new List<ScoreRow>
        {
            new("ci-p50", "CI duration, median", Format(Percentile(stats.CiDurations, 0.5)), $"{stats.CiDurations.Count} recent CI runs"),
            new("ci-p90", "CI duration, p90", Format(Percentile(stats.CiDurations, 0.9)), "9 in 10 runs finish faster"),
            new("pr-to-merge", "PR open to merge, median", Format(Percentile(stats.OpenToMerge, 0.5)), $"{stats.OpenToMerge.Count} merged PRs"),
            new("agent-clean", "Agent PRs merged without changes",
                stats.AgentPullsMerged == 0 ? "none yet" : $"{stats.AgentPullsMergedWithoutChanges} of {stats.AgentPullsMerged}",
                "No review asked for changes"),
            new("rollbacks", "Rollbacks, last 30 days", rollbacks.ToString(CultureInfo.InvariantCulture), "Deploys undone automatically"),
            new("cost", "Cost, month to date", spend is null ? "unavailable" : Money(spend.MonthToDate), spend is null ? "" : $"{Money(spend.Today)} today"),
        };
        return new Scoreboard(rows, time.GetUtcNow());
    }

    public static TimeSpan? Percentile(IReadOnlyList<TimeSpan> values, double p)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    public static string Format(TimeSpan? t) => t switch
    {
        null => "no data",
        { TotalMinutes: < 1 } v => $"{v.Seconds} s",
        { TotalHours: < 1 } v => $"{(int)v.TotalMinutes} min {v.Seconds} s",
        { TotalDays: < 1 } v => $"{(int)v.TotalHours} h {v.Minutes} min",
        var v => $"{(int)v.Value.TotalDays} d {v.Value.Hours} h",
    };

    public static string Money(decimal amount) => (amount < 0 ? "-$" : "$") + Math.Abs(amount).ToString("N2", CultureInfo.InvariantCulture);
}
