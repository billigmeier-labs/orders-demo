using System.Globalization;
using System.Text.Json;
using Orders.Console.Integrations;

namespace Orders.Console.Status;

/// <summary>One scoreboard number, with the spec's target (docs: "Goal") and whether it is met; null when there is no target or no data.</summary>
public sealed record ScoreRow(string Id, string Name, string Value, string Hint, string? Target = null, bool? Pass = null);

/// <summary>A CI job's p50 and p90 over recent runs.</summary>
public sealed record JobTiming(string Name, TimeSpan? P50, TimeSpan? P90, int Runs);

/// <summary>The scoreboard at one moment; also what is saved per demo run (runs/&lt;N&gt;.json).</summary>
public sealed record Scoreboard(IReadOnlyList<ScoreRow> Rows, DateTimeOffset At)
{
    public string? Run { get; init; }

    public IReadOnlyList<JobTiming> Jobs { get; init; } = [];

    public ScoreRow? Row(string id) => Rows.FirstOrDefault(r => r.Id == id);
}

/// <summary>What showing the scoreboard did: the numbers, and where the run's snapshot was saved (or why not).</summary>
public sealed record ScoreboardShown(Scoreboard Board, SavedSnapshot? Saved, string? SaveError);

/// <summary>
/// Delivery metrics for the scoreboard scenario, measured against the spec's targets, from GitHub (PRs, timelines,
/// Actions runs and jobs), the deploy pipeline and Cost Explorer. Showing it saves a snapshot for the demo run.
/// </summary>
public sealed class ScoreboardService(IGitHubService github, ICostService cost, IDemoRuns runs, ConsoleOptions options, TimeProvider time)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly TimeSpan CiTarget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MergeToProdTarget = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReviewTarget = TimeSpan.FromMinutes(10);
    private const int PullSizeTarget = 400;

    /// <summary>The last scoreboard shown, for the Present page.</summary>
    public ScoreboardShown? Latest { get; private set; }

    /// <summary>The last list of past runs read, for the Present page.</summary>
    public IReadOnlyList<Scoreboard>? LatestPastRuns { get; private set; }

    public async Task<Scoreboard> GetAsync(CancellationToken ct)
    {
        var stats = await github.GetDeliveryStatsAsync(ct);
        var run = await OptionalAsync(runs.GetCurrentRunAsync, ct);
        var spend = await OptionalAsync(cost.GetCostAsync, ct);
        var runCost = run is null ? null : await OptionalAsync<decimal?>(async c => await cost.GetRunCostAsync(run, c), ct);

        var ciP90 = Percentile(stats.CiDurations, 0.9);
        var size = Median(stats.PullSizes);
        var openToMerge = Percentile(stats.OpenToMerge, 0.5);
        var mergeToProd = Percentile(stats.MergeToProduction, 0.5);
        var review = Percentile(stats.HumanReviewTimes, 0.5);
        var cleanShare = stats.AgentPullsMerged == 0 ? (double?)null : (double)stats.AgentPullsMergedWithoutBlock / stats.AgentPullsMerged;
        var failureShare = stats.ProdDeploys == 0 ? (double?)null : (double)stats.ProdDeployFailures / stats.ProdDeploys;

        var rows = new List<ScoreRow>
        {
            new("ci-p50", "CI on a PR, median", Format(Percentile(stats.CiDurations, 0.5)), $"{stats.CiDurations.Count} recent CI runs on PRs and in the merge queue"),
            new("ci-p90", "CI on a PR, p90", Format(ciP90), "9 in 10 runs finish faster; the slowest job sets the time", "Under 5 min", ciP90 is null ? null : ciP90 < CiTarget),
            new("pr-size", "PR size, median", size is null ? "no data" : $"{size} lines", $"Lines added plus deleted, {stats.PullSizes.Count} merged PRs", $"Under {PullSizeTarget} lines", size is null ? null : size < PullSizeTarget),
            new("pr-to-merge", "PR open to merged, median", Format(openToMerge), $"{stats.OpenToMerge.Count} merged PRs", "Same day", openToMerge is null ? null : openToMerge < TimeSpan.FromDays(1)),
            new("queue-wait", "Merge queue wait, median", Format(Percentile(stats.MergeQueueWaits, 0.5)), "Added to the queue until merged: the re-test on top of main"),
            new("merge-to-prod", "Merge to running in production, median", Format(mergeToProd), "Merged until the deploy's production step finished, with no one pressing a button", "Automatic, under 15 min", mergeToProd is null ? null : mergeToProd < MergeToProdTarget),
            new("agent-clean", "Agent PRs merged without an AI-review block",
                stats.AgentPullsMerged == 0 ? "none yet" : $"{stats.AgentPullsMergedWithoutBlock} of {stats.AgentPullsMerged} ({Share(cleanShare!.Value)})",
                "Never labeled ai-review:blocked, and no review asked for changes", "Over 70%", cleanShare is null ? null : cleanShare > 0.7),
            new("review-time", "Human review time per PR, median", Format(review), $"Review asked for until a person reviewed, {stats.HumanReviewTimes.Count} PRs that needed one", "Under 10 min", review is null ? null : review < ReviewTarget),
            new("deploys", "Production deploys, last 30 days", stats.ProdDeploys.ToString(CultureInfo.InvariantCulture), "Deploy runs that reached production"),
            new("change-failure", "Change failure rate",
                failureShare is null ? "no deploys yet" : $"{stats.ProdDeployFailures} of {stats.ProdDeploys} ({Share(failureShare!.Value)})",
                "Production deploys that failed, rolled back or were reverted", "Under 10% of deploys", failureShare is null ? null : failureShare < 0.1),
            new("rollbacks", "Prod rollbacks, last 30 days", stats.ProdRollbacks.ToString(CultureInfo.InvariantCulture), "Production deploys ECS rolled back"),
            new("flaky", "Flaky tests", stats.FlakyTests?.ToString(CultureInfo.InvariantCulture) ?? "unavailable", "Open issues labeled flaky: quarantined and tracked, never retried until green"),
            new("mutation", "Mutation score", stats.MutationScore is { } m ? $"{m.ToString("0.#", CultureInfo.InvariantCulture)}%" : "no nightly run yet",
                "Share of deliberate bugs the tests caught, last nightly run", $"At least {options.MutationScoreTarget.ToString("0.#", CultureInfo.InvariantCulture)}%",
                stats.MutationScore is { } score ? score >= options.MutationScoreTarget : null),
            new("run-cost", "Cost of this demo run", runCost is { } rc ? Money(rc) : "unavailable", run is null ? "Demo run unknown" : $"Resources tagged demo-run={run}; Cost Explorer is about a day behind"),
            new("cost", "Cost, month to date", spend is null ? "unavailable" : Money(spend.MonthToDate), spend is null ? "" : $"{Money(spend.Today)} today, project=orders-demo"),
        };

        var jobs = stats.CiJobDurations
            .Select(kv => new JobTiming(kv.Key, Percentile(kv.Value, 0.5), Percentile(kv.Value, 0.9), kv.Value.Count))
            .OrderByDescending(j => j.P90).ThenBy(j => j.Name, StringComparer.Ordinal)
            .ToList();
        return new Scoreboard(rows, time.GetUtcNow()) { Run = run, Jobs = jobs };
    }

    /// <summary>The scoreboard, saved as this demo run's snapshot. Saving is best effort: the numbers show either way.</summary>
    public async Task<ScoreboardShown> ShowAsync(CancellationToken ct)
    {
        var board = await GetAsync(ct);
        SavedSnapshot? saved = null;
        string? error = null;
        if (board.Run is null)
        {
            error = "the demo run number is unknown";
        }
        else
        {
            try
            {
                saved = await runs.SaveAsync(board.Run, JsonSerializer.Serialize(board, Json), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }
        }

        Latest = new ScoreboardShown(board, saved, error);
        return Latest;
    }

    /// <summary>Saved snapshots of earlier runs, newest first; unreadable ones are skipped.</summary>
    public async Task<IReadOnlyList<Scoreboard>> PastRunsAsync(CancellationToken ct)
    {
        var boards = new List<Scoreboard>();
        foreach (var file in await runs.ListAsync(ct))
        {
            try
            {
                if (JsonSerializer.Deserialize<Scoreboard>(file.Json, Json) is { } board)
                {
                    boards.Add(board with { Run = board.Run ?? file.Run });
                }
            }
            catch (JsonException)
            {
                // Not a snapshot this console wrote.
            }
        }

        LatestPastRuns = boards;
        return boards;
    }

    private static async Task<T?> OptionalAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        try
        {
            return await read(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cost and the run number are optional on the scoreboard.
            return default;
        }
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

    public static int? Median(IReadOnlyList<int> values) =>
        values.Count == 0 ? null : values.Order().ElementAt((values.Count - 1) / 2);

    public static string Format(TimeSpan? t) => t switch
    {
        null => "no data",
        { TotalMinutes: < 1 } v => $"{v.Seconds} s",
        { TotalHours: < 1 } v => $"{(int)v.TotalMinutes} min {v.Seconds} s",
        { TotalDays: < 1 } v => $"{(int)v.TotalHours} h {v.Minutes} min",
        var v => $"{(int)v.Value.TotalDays} d {v.Value.Hours} h",
    };

    public static string Share(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Money(decimal amount) => (amount < 0 ? "-$" : "$") + Math.Abs(amount).ToString("N2", CultureInfo.InvariantCulture);
}
