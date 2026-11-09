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
}

public interface IEnvironmentProbe
{
    Task<EnvironmentHealth> CheckAsync(string environment, CancellationToken ct);
}

public interface IMonitoringService
{
    Task<IReadOnlyList<AlarmState>> GetAlarmsAsync(CancellationToken ct);

    Task<IReadOnlyList<LogLine>> GetRecentErrorsAsync(CancellationToken ct);
}

public interface IDeploymentService
{
    Task<IReadOnlyList<DeployRecord>> GetRecentDeploysAsync(CancellationToken ct);
}

public interface ICostService
{
    Task<CostSummary> GetCostAsync(CancellationToken ct);
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

public sealed record WorkflowRunSummary(string Workflow, string Status, string? Conclusion, string Title, string Branch, string Url, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, long Id = 0)
{
    public TimeSpan? Duration => Status == "completed" ? UpdatedAt - StartedAt : null;
}

public sealed record LabelCount(string Label, int Count);

public sealed record GitHubActivity(
    IReadOnlyList<LabelCount> OpenIssuesByLabel,
    int OpenIssues,
    IReadOnlyList<PullRequestSummary> AgentPulls,
    IReadOnlyList<WorkflowRunSummary> LatestRuns);

public sealed record DeliveryStats(
    IReadOnlyList<TimeSpan> CiDurations,
    IReadOnlyList<TimeSpan> OpenToMerge,
    int AgentPullsMerged,
    int AgentPullsMergedWithoutBlock,
    IReadOnlyList<WorkflowRunSummary> DeployRuns,
    int ProdRollbacks);

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
