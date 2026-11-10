using System.Globalization;
using Orders.Console.Integrations;
using Orders.Console.Status;

namespace Orders.Console.Scenarios;

/// <summary>The actions a scenario step (or reset) can run, by <c>kind</c>.</summary>
public sealed class ScenarioActions(
    IGitHubService github,
    ILoadGenerator load,
    IFeatureFlagService flags,
    ScoreboardService scoreboard,
    StatusService status) : IScenarioActions
{
    public static readonly IReadOnlySet<string> KnownKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "github.createIssue",
        "github.closeIssue",
        "github.openPullRequest",
        "github.closePullRequest",
        "github.commentOnPullRequest",
        "github.listIssues",
        "github.listAwaitingReview",
        "load.start",
        "load.stop",
        "flags.set",
        "scoreboard.show",
        "scoreboard.pastRuns",
        "note.show",
    };

    public IReadOnlySet<string> Kinds => KnownKinds;

    public async Task<ActionOutcome> ExecuteAsync(string kind, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        string Arg(string name) => args.TryGetValue(name, out var v) && v.Length > 0
            ? v
            : throw new InvalidOperationException($"Action {kind} needs '{name}'.");
        string ArgOr(string name, string fallback) => args.TryGetValue(name, out var v) && v.Length > 0 ? v : fallback;
        int Number(string name) => int.Parse(Arg(name), CultureInfo.InvariantCulture);

        switch (kind)
        {
            case "github.createIssue":
                {
                    var labels = ArgOr("labels", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var issue = await github.CreateIssueAsync(new NewIssue(Arg("title"), ArgOr("body", ""), labels), ct);
                    status.Invalidate(StatusService.GitHubPanel);
                    return new ActionOutcome(
                        new Dictionary<string, string> { ["issue"] = issue.Number.ToString(CultureInfo.InvariantCulture), ["issueUrl"] = issue.Url },
                        [new ActionLine($"Opened issue #{issue.Number}", issue.Url)]);
                }

            case "github.closeIssue":
                await github.CloseIssueAsync(Number("number"), ct);
                return ActionOutcome.Of(new ActionLine($"Closed issue #{Arg("number")}"));

            case "github.openPullRequest":
                {
                    var prLabels = ArgOr("labels", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var pr = await github.OpenPullRequestAsync(new NewPullRequest(Arg("head"), ArgOr("base", "main"), Arg("title"), ArgOr("body", ""), prLabels), ct);
                    status.Invalidate(StatusService.GitHubPanel);
                    return new ActionOutcome(
                        new Dictionary<string, string> { ["pr"] = pr.Number.ToString(CultureInfo.InvariantCulture), ["prUrl"] = pr.Url },
                        [new ActionLine($"Opened pull request #{pr.Number}", pr.Url)]);
                }

            case "github.closePullRequest":
                await github.ClosePullRequestAsync(Number("number"), ct);
                return ActionOutcome.Of(new ActionLine($"Closed pull request #{Arg("number")}"));

            case "github.commentOnPullRequest":
                await github.CommentOnPullRequestAsync(Number("number"), Arg("body"), ct);
                return ActionOutcome.Of(new ActionLine($"Commented on pull request #{Arg("number")}"));

            case "github.listIssues":
                {
                    var label = Arg("label");
                    var state = ArgOr("state", "open");
                    var issues = await github.ListIssuesAsync(label, ct, state);
                    var lines = new List<ActionLine>();
                    if (issues.Count == 0)
                    {
                        lines.Add(new ActionLine(state == "all" ? $"No issues labeled {label} yet." : $"No open issues labeled {label}."));
                    }

                    foreach (var item in issues)
                    {
                        var closed = item.Issue.State == "closed" ? " (closed)" : "";
                        lines.Add(new ActionLine($"#{item.Issue.Number} {item.Issue.Title}{closed}", item.Issue.Url));
                        if (item.Pulls.Count == 0)
                        {
                            lines.Add(new ActionLine("    no agent PR yet"));
                        }

                        foreach (var pr in item.Pulls)
                        {
                            lines.Add(new ActionLine($"    PR #{pr.Number} by {pr.Author}: {pr.Title} ({pr.State})", pr.Url));
                        }
                    }

                    return new ActionOutcome(new Dictionary<string, string>(), lines);
                }

            case "github.listAwaitingReview":
                {
                    var prs = await github.ListAwaitingReviewAsync(ct);
                    var lines = prs.Count == 0
                        ? [new ActionLine("No PRs are waiting for a human review.")]
                        : prs.Select(p => new ActionLine($"PR #{p.Number} by {p.Author}: {p.Title}, waiting for review", p.Url)).ToList();
                    return new ActionOutcome(new Dictionary<string, string>(), lines);
                }

            case "load.start":
                {
                    var scenario = ArgOr("scenario", "normal");
                    var minutes = int.Parse(ArgOr("durationMinutes", "15"), CultureInfo.InvariantCulture);
                    await load.StartAsync(scenario, minutes, ct);
                    status.Invalidate(StatusService.LoadPanel);
                    return ActionOutcome.Of(new ActionLine($"Load generator started: {scenario} for {minutes} min"));
                }

            case "load.stop":
                await load.StopAsync(ct);
                status.Invalidate(StatusService.LoadPanel);
                return ActionOutcome.Of(new ActionLine("Load generator stopped"));

            case "flags.set":
                {
                    var flag = Arg("flag");
                    var enabled = bool.Parse(Arg("enabled"));
                    await flags.SetFlagAsync(flag, enabled, ct);
                    status.Invalidate(StatusService.FlagsPanel);
                    return ActionOutcome.Of(new ActionLine($"Feature flag {flag} turned {(enabled ? "on" : "off")}"));
                }

            case "scoreboard.show":
                {
                    var shown = await scoreboard.ShowAsync(ct);
                    var lines = shown.Board.Rows.Select(r => new ActionLine(
                        $"{r.Name}: {r.Value}" + (r.Target is null ? "" : $" (target {r.Target}: {(r.Pass switch { true => "met", false => "missed", null => "no data" })})"))).ToList();
                    // Always set, so later steps can mention the snapshot whether or not it was saved.
                    var vars = new Dictionary<string, string>
                    {
                        ["run"] = shown.Board.Run ?? "unknown",
                        ["snapshot"] = shown.Saved?.Location ?? "not saved",
                        ["snapshotUrl"] = shown.Saved?.Url ?? "",
                    };
                    lines.Add(shown.Saved is { } saved
                        ? new ActionLine($"Saved as run {shown.Board.Run}: {saved.Location}", saved.Url)
                        : new ActionLine($"Not saved: {shown.SaveError}"));

                    return new ActionOutcome(vars, lines);
                }

            case "scoreboard.pastRuns":
                {
                    var past = await scoreboard.PastRunsAsync(ct);
                    var lines = past.Count == 0
                        ? [new ActionLine("No saved runs yet.")]
                        : past.Select(b => new ActionLine($"Run {b.Run}, {b.At:MMM d}: " + string.Join(", ",
                            b.Rows.Where(r => r.Id is "ci-p90" or "pr-to-merge" or "change-failure" or "run-cost").Select(r => $"{r.Name} {r.Value}")))).ToList();
                    return new ActionOutcome(new Dictionary<string, string>(), lines);
                }

            case "note.show":
                {
                    // Lines of text for the audience; the last one links to "url" when it is set.
                    var text = Arg("text").Split('\n');
                    var url = ArgOr("url", "");
                    var lines = text.Select((line, i) => new ActionLine(line, i == text.Length - 1 && url.Length > 0 ? url : null)).ToList();
                    return new ActionOutcome(new Dictionary<string, string>(), lines);
                }

            default:
                throw new InvalidOperationException($"Unknown action kind '{kind}'.");
        }
    }
}

/// <summary>Live conditions a wait can check instead of an event.</summary>
public sealed class ConditionEvaluator(IMonitoringService monitoring, IGitHubService github) : IConditionEvaluator
{
    /// <summary>Every alarm is OK.</summary>
    public const string AlarmsOk = "alarms-ok";

    /// <summary>The load balancer reports at least <see cref="TrafficThreshold"/> requests a minute on production.</summary>
    public const string ProdTraffic = "prod-traffic";

    /// <summary>The PR in the subject (pr#N) can't merge without a review, e.g. a code owner's.</summary>
    public const string ReviewRequired = "review-required";

    /// <summary>Requests a minute that only the load generator makes (normal load is about 300).</summary>
    public const double TrafficThreshold = 100;

    public IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal) { AlarmsOk, ProdTraffic, ReviewRequired };

    public async Task<bool> EvaluateAsync(string name, string? subject, CancellationToken ct) => name switch
    {
        AlarmsOk => (await monitoring.GetAlarmsAsync(ct)).All(a => a.State == "OK"),
        ProdTraffic => (await monitoring.GetTrafficAsync(ct)).Any(t => t.Environment == "prod" && t.LatestRequests >= TrafficThreshold),
        ReviewRequired => subject is not null && Matching.NumberIn(subject) is { } n
            && (await github.GetReviewStateAsync(int.Parse(n, System.Globalization.CultureInfo.InvariantCulture), ct)).ReviewRequired,
        _ => throw new InvalidOperationException($"Unknown condition '{name}'."),
    };
}
