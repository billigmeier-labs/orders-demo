using Orders.Console.Domain;

namespace Orders.Console.Integrations;

// Every external dependency sits behind one of these interfaces. Real implementations live in
// Integrations/GitHub, Integrations/Aws and Integrations/Http; Integrations/Fakes/FakeWorld implements
// all of them with simulated data when CONSOLE_FAKES=true.

/// <summary>A place the console polls for new <see cref="ConsoleEvent"/>s.</summary>
public interface IEventSource
{
    string Name { get; }

    TimeSpan Interval { get; }

    Task<IReadOnlyList<ConsoleEvent>> PollAsync(DateTimeOffset since, CancellationToken ct);
}

public interface IGitHubService
{
    Task<IssueRef> CreateIssueAsync(NewIssue issue, CancellationToken ct);

    Task CloseIssueAsync(int number, CancellationToken ct);

    Task<PullRequestRef> OpenPullRequestAsync(NewPullRequest pr, CancellationToken ct);

    Task ClosePullRequestAsync(int number, CancellationToken ct);

    Task CommentOnPullRequestAsync(int number, string body, CancellationToken ct);

    Task<GitHubActivity> GetActivityAsync(CancellationToken ct);

    /// <summary>Open issues with the label, each with the open or merged PRs that reference it.</summary>
    /// <param name="state">"open" (default) or "all" (recent issues, newest first, closed ones included).</param>
    Task<IReadOnlyList<IssueWithPulls>> ListIssuesAsync(string label, CancellationToken ct, string state = "open");

    /// <summary>Open PRs that are waiting on a human review.</summary>
    Task<IReadOnlyList<PullRequestSummary>> ListAwaitingReviewAsync(CancellationToken ct);

    Task<DeliveryStats> GetDeliveryStatsAsync(CancellationToken ct);

    /// <summary>Whether the PR still needs a review (GitHub's reviewDecision), e.g. a code owner's for .github/ changes.</summary>
    Task<PullReviewState> GetReviewStateAsync(int number, CancellationToken ct);
}

public interface IEnvironmentProbe
{
    Task<EnvironmentHealth> CheckAsync(string environment, CancellationToken ct);
}

public interface IMonitoringService
{
    Task<IReadOnlyList<AlarmState>> GetAlarmsAsync(CancellationToken ct);

    Task<IReadOnlyList<LogLine>> GetRecentErrorsAsync(CancellationToken ct);

    /// <summary>Request rate, 5xx rate and latency for prod and staging over the last minutes, from the load balancer.</summary>
    Task<IReadOnlyList<EnvironmentTraffic>> GetTrafficAsync(CancellationToken ct);
}

public interface IDeploymentService
{
    Task<IReadOnlyList<DeployRecord>> GetRecentDeploysAsync(CancellationToken ct);
}

public interface ICostService
{
    Task<CostSummary> GetCostAsync(CancellationToken ct);

    /// <summary>Cost of everything tagged demo-run=<paramref name="run"/> (Cost Explorer, about a day behind).</summary>
    Task<decimal> GetRunCostAsync(string run, CancellationToken ct);
}

/// <summary>The current demo run (SSM /orders-demo/demo-run) and the scoreboard snapshots kept per run (S3 runs/&lt;N&gt;.json).</summary>
public interface IDemoRuns
{
    Task<string?> GetCurrentRunAsync(CancellationToken ct);

    /// <summary>Saves the snapshot as runs/&lt;run&gt;.json and returns where it went.</summary>
    Task<SavedSnapshot> SaveAsync(string run, string json, CancellationToken ct);

    /// <summary>Saved runs, newest first, each with its snapshot.</summary>
    Task<IReadOnlyList<RunSnapshotFile>> ListAsync(CancellationToken ct);
}

public interface IFeatureFlagService
{
    Task<IReadOnlyList<FeatureFlag>> GetFlagsAsync(CancellationToken ct);

    Task SetFlagAsync(string name, bool enabled, CancellationToken ct);
}

public interface ILoadGenerator
{
    Task<LoadStatus> GetStatusAsync(CancellationToken ct);

    Task StartAsync(string scenario, int durationMinutes, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

public sealed record NewIssue(string Title, string Body, IReadOnlyList<string> Labels);

public sealed record IssueRef(int Number, string Url);

public sealed record NewPullRequest(string Head, string Base, string Title, string Body, IReadOnlyList<string>? Labels = null);

public sealed record PullRequestRef(int Number, string Url);

public sealed record IssueSummary(int Number, string Title, IReadOnlyList<string> Labels, string Author, string Url, DateTimeOffset CreatedAt, string State = "open");

public sealed record PullRequestSummary(int Number, string Title, string Author, string State, string Url, DateTimeOffset CreatedAt, DateTimeOffset? MergedAt);

public sealed record IssueWithPulls(IssueSummary Issue, IReadOnlyList<PullRequestSummary> Pulls);

public sealed record WorkflowRunSummary(string Workflow, string Status, string? Conclusion, string Title, string Branch, string Url, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, long Id = 0, string? HeadSha = null, string? Event = null)
{
    public TimeSpan? Duration => Status == "completed" ? UpdatedAt - StartedAt : null;
}

public sealed record LabelCount(string Label, int Count);

/// <param name="TouchesCodeOwned">The PR changes a path .github/CODEOWNERS gives to a person. GitHub doesn't ask the
/// author to review their own PR, so a PR the code owner opened shows no review decision but still can't merge.</param>
public sealed record PullReviewState(int Number, string? ReviewDecision, string? MergeState, bool TouchesCodeOwned = false)
{
    public bool ReviewRequired => ReviewDecision == "REVIEW_REQUIRED" || (TouchesCodeOwned && ReviewDecision != "APPROVED");
}

public sealed record EnvironmentTraffic(
    string Environment,
    double RequestsPerMinute,
    double ErrorRate,
    TimeSpan? P95Latency,
    IReadOnlyList<TrafficPoint> Minutes)
{
    /// <summary>Requests in the latest minute the load balancer has reported.</summary>
    public double LatestRequests => Minutes.Count == 0 ? 0 : Minutes[^1].Requests;
}

/// <summary>One minute of load balancer metrics.</summary>
public sealed record TrafficPoint(DateTimeOffset At, double Requests, double Errors5xx, TimeSpan? P95Latency);

public sealed record SavedSnapshot(string Location, string? Url);

public sealed record RunSnapshotFile(string Run, DateTimeOffset SavedAt, string Json);

/// <param name="AgentPulls">The agent's most recent PRs, any state.</param>
/// <param name="AgentPullsInFlight">The agent's open PRs.</param>
public sealed record GitHubActivity(
    IReadOnlyList<LabelCount> OpenIssuesByLabel,
    int OpenIssues,
    IReadOnlyList<PullRequestSummary> AgentPulls,
    IReadOnlyList<WorkflowRunSummary> LatestRuns,
    IReadOnlyList<PullRequestSummary>? AgentPullsInFlight = null)
{
    /// <summary>The labels triage sets, in the order the Status page shows them; a trailing ':' matches a family.</summary>
    public static readonly IReadOnlyList<string> TriageLabels = ["agent", "needs-human", "risk:", "difficulty:"];

    /// <summary>Open issues by the labels triage sets (agent, needs-human, risk:*, difficulty:*), in that order.</summary>
    public IReadOnlyList<LabelCount> ByTriageLabel => OpenIssuesByLabel
        .Where(l => TriageRank(l.Label) >= 0)
        .OrderBy(l => TriageRank(l.Label)).ThenBy(l => l.Label, StringComparer.Ordinal)
        .ToList();

    /// <summary>Open issues by every other label.</summary>
    public IReadOnlyList<LabelCount> ByOtherLabel => OpenIssuesByLabel.Where(l => TriageRank(l.Label) < 0).ToList();

    private static int TriageRank(string label)
    {
        for (var i = 0; i < TriageLabels.Count; i++)
        {
            var t = TriageLabels[i];
            if (label == t || (t.EndsWith(':') && label.StartsWith(t, StringComparison.Ordinal)))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Delivery numbers for the scoreboard, from GitHub's API and Actions data.</summary>
/// <param name="CiDurations">Whole CI runs (pull_request and merge_group), newest first.</param>
/// <param name="CiJobDurations">Each CI job's durations, by job name.</param>
/// <param name="PullSizes">Changed lines (additions + deletions) per merged PR.</param>
/// <param name="MergeQueueWaits">Added to the merge queue → merged, per merged PR that went through the queue.</param>
/// <param name="HumanReviewTimes">Review requested (or ready for review) → first human review, on PRs that needed one.</param>
/// <param name="MergeToProduction">PR merged → that commit live in production (the deploy's Production job done).</param>
/// <param name="ProdDeploys">Deploy runs that reached the Production job in the last 30 days.</param>
/// <param name="ProdDeployFailures">Of those: rolled back, failed, or later reverted.</param>
/// <param name="FlakyTests">Open issues labeled flaky.</param>
/// <param name="MutationScore">The latest nightly mutation score in percent, if there is one.</param>
public sealed record DeliveryStats(
    IReadOnlyList<TimeSpan> CiDurations,
    IReadOnlyList<TimeSpan> OpenToMerge,
    int AgentPullsMerged,
    int AgentPullsMergedWithoutBlock,
    IReadOnlyList<WorkflowRunSummary> DeployRuns,
    int ProdRollbacks)
{
    public IReadOnlyDictionary<string, IReadOnlyList<TimeSpan>> CiJobDurations { get; init; } = new Dictionary<string, IReadOnlyList<TimeSpan>>();

    public IReadOnlyList<int> PullSizes { get; init; } = [];

    public IReadOnlyList<TimeSpan> MergeQueueWaits { get; init; } = [];

    public IReadOnlyList<TimeSpan> HumanReviewTimes { get; init; } = [];

    public IReadOnlyList<TimeSpan> MergeToProduction { get; init; } = [];

    public int ProdDeploys { get; init; }

    public int ProdDeployFailures { get; init; }

    public int? FlakyTests { get; init; }

    public double? MutationScore { get; init; }
}

public sealed record EnvironmentHealth(string Environment, string Url, bool Ready, string Detail, string? Version, string? Commit, TimeSpan? Latency);

public sealed record AlarmState(string Name, string State, string? Reason, DateTimeOffset UpdatedAt, string? Url);

public sealed record LogLine(DateTimeOffset At, string Message, string? Stream);

/// <summary>A deployment of one service. <paramref name="Version"/> is the task definition (or app version);
/// <paramref name="Commit"/> is the full commit SHA of the image, which is also its tag, when known.</summary>
public sealed record DeployRecord(string Environment, string Version, string State, DateTimeOffset At, string? Url, string? Commit = null)
{
    public string? ShortCommit => Commit is { Length: > 7 } c ? c[..7] : Commit;
}

public sealed record CostSummary(decimal MonthToDate, decimal Today, string Currency, DateTimeOffset AsOf);

public sealed record FeatureFlag(string Name, bool Enabled, string? Description);

public sealed record LoadStatus(bool Running, string? Scenario, DateTimeOffset? StartedAt, DateTimeOffset? EndsAt, string? TaskId);
