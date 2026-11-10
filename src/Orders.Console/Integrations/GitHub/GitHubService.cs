using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orders.Console.Domain;

namespace Orders.Console.Integrations.GitHub;

/// <summary>GitHub REST (and one GraphQL call) over HttpClient, authenticated with GITHUB_TOKEN.</summary>
public sealed partial class GitHubService(HttpClient http, ConsoleOptions options, TimeProvider time, ILogger<GitHubService> logger)
    : IGitHubService, IEventSource
{
    private readonly string _repo = $"/repos/{options.GitHubRepository}";
    private const string AiReviewBlockedLabel = "ai-review:blocked";
    private const string ProdJobPrefix = "Production";
    private const string ProdRollbackMessage = "Production rolled back to the previous version";

    private const string StagingJobPrefix = "Staging";
    private const string FlakyLabel = "flaky";
    private const string MutationWorkflow = "mutation.yml";
    private const string MutationScoreTitle = "mutation-score";

    private readonly ConcurrentDictionary<(long Run, DateTimeOffset UpdatedAt), bool> _rollbacks = new();
    // Completed runs and merged PRs never change, so what was read about them is kept.
    private readonly ConcurrentDictionary<(long Run, DateTimeOffset UpdatedAt), IReadOnlyList<JobInfo>> _jobs = new();
    private readonly ConcurrentDictionary<int, PullFacts> _pullFacts = new();
    private readonly ConcurrentDictionary<long, double?> _mutationScores = new();
    private readonly ConcurrentDictionary<int, (DateTimeOffset Until, PullReviewState State)> _reviewStates = new();
    private (DateTimeOffset Until, DeliveryStats Stats)? _stats;

    public string Name => "github";

    public TimeSpan Interval => options.GitHubPollInterval;

    public static void Configure(HttpClient http, ConsoleOptions options)
    {
        http.BaseAddress = new Uri(options.GitHubApiUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("orders-demo-console");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (options.GitHubToken is not null)
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", options.GitHubToken);
        }
    }

    public async Task<IssueRef> CreateIssueAsync(NewIssue issue, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"{_repo}/issues", new { title = issue.Title, body = issue.Body, labels = issue.Labels }, ct);
        return new IssueRef(json.GetProperty("number").GetInt32(), json.GetProperty("html_url").GetString()!);
    }

    public Task CloseIssueAsync(int number, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"{_repo}/issues/{number}", new { state = "closed", state_reason = "not_planned" }, ct);

    public async Task<PullRequestRef> OpenPullRequestAsync(NewPullRequest pr, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"{_repo}/pulls", new { title = pr.Title, head = pr.Head, @base = pr.Base, body = pr.Body }, ct);
        var number = json.GetProperty("number").GetInt32();
        var nodeId = json.GetProperty("node_id").GetString();
        if (pr.Labels is { Count: > 0 })
        {
            await SendAsync(HttpMethod.Post, $"{_repo}/issues/{number}/labels", new { labels = pr.Labels }, ct);
        }

        try
        {
            // Auto-merge puts the PR into the merge queue as soon as its required checks pass.
            await SendAsync(HttpMethod.Post, "graphql", new
            {
                query = "mutation($id: ID!) { enablePullRequestAutoMerge(input: { pullRequestId: $id, mergeMethod: SQUASH }) { clientMutationId } }",
                variables = new { id = nodeId },
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAutoMergeFailed(ex, number);
        }

        return new PullRequestRef(number, json.GetProperty("html_url").GetString()!);
    }

    public Task ClosePullRequestAsync(int number, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"{_repo}/pulls/{number}", new { state = "closed" }, ct);

    public Task CommentOnPullRequestAsync(int number, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{_repo}/issues/{number}/comments", new { body }, ct);

    public async Task<GitHubActivity> GetActivityAsync(CancellationToken ct)
    {
        var issues = (await GetArrayAsync($"{_repo}/issues?state=open&per_page=100", ct)).Where(i => !i.TryGetProperty("pull_request", out _)).ToList();
        var labels = issues.SelectMany(i => i.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()!))
            .GroupBy(l => l).Select(g => new LabelCount(g.Key, g.Count()))
            .OrderByDescending(l => l.Count).ThenBy(l => l.Label, StringComparer.Ordinal).ToList();
        var pulls = await GetArrayAsync($"{_repo}/pulls?state=all&sort=created&direction=desc&per_page=50", ct);
        var agentPulls = pulls.Where(p => Login(p) == options.AgentLogin).Take(6).Select(ToPull).ToList();
        var inFlight = pulls.Where(p => Login(p) == options.AgentLogin && p.GetProperty("state").GetString() == "open").Select(ToPull).ToList();
        var runs = new List<WorkflowRunSummary>();
        foreach (var workflow in options.Workflows)
        {
            var latest = await WorkflowRunsAsync(workflow, 1, ct);
            runs.AddRange(latest);
        }

        return new GitHubActivity(labels, issues.Count, agentPulls, runs, inFlight);
    }

    public async Task<IReadOnlyList<IssueWithPulls>> ListIssuesAsync(string label, CancellationToken ct, string state = "open")
    {
        var issueState = state == "all" ? "all" : "open";
        var issues = (await GetArrayAsync($"{_repo}/issues?state={issueState}&labels={Uri.EscapeDataString(label)}&sort=created&direction=desc&per_page=50", ct))
            .Where(i => !i.TryGetProperty("pull_request", out _)).Take(issueState == "all" ? 6 : 50).ToList();
        var pulls = await GetArrayAsync($"{_repo}/pulls?state=all&sort=created&direction=desc&per_page=100", ct);
        return issues.Select(i =>
        {
            var number = i.GetProperty("number").GetInt32();
            var linked = pulls.Where(p => References(p).Contains(number)).Select(ToPull).ToList();
            return new IssueWithPulls(ToIssue(i), linked);
        }).ToList();
    }

    public async Task<IReadOnlyList<PullRequestSummary>> ListAwaitingReviewAsync(CancellationToken ct)
    {
        var pulls = await GetArrayAsync($"{_repo}/pulls?state=open&per_page=50", ct);
        return pulls.Where(p => !p.GetProperty("draft").GetBoolean()
                && (p.GetProperty("requested_reviewers").GetArrayLength() > 0 || p.GetProperty("requested_teams").GetArrayLength() > 0))
            .Select(ToPull).ToList();
    }

    public async Task<DeliveryStats> GetDeliveryStatsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_stats is { } s && s.Until > now)
        {
            return s.Stats;
        }

        // CI: whole runs on PRs and in the merge queue, and each job's time.
        var ciRuns = (await WorkflowRunsAsync(options.Workflows.Count > 0 ? options.Workflows[0] : "ci.yml", 30, ct, "&status=completed"))
            .Where(r => r.Event is null or "pull_request" or "merge_group").ToList();
        var ci = ciRuns.Select(r => r.Duration).OfType<TimeSpan>().ToList();
        var jobDurations = new Dictionary<string, List<TimeSpan>>(StringComparer.Ordinal);
        foreach (var run in ciRuns)
        {
            foreach (var job in await RunJobsAsync(run, ct) ?? [])
            {
                if (job.Duration is { } d && job.Conclusion is "success" or "failure")
                {
                    (jobDurations.TryGetValue(job.Name, out var list) ? list : jobDurations[job.Name] = []).Add(d);
                }
            }
        }

        var closed = await GetArrayAsync($"{_repo}/pulls?state=closed&sort=updated&direction=desc&per_page=50", ct);
        var merged = closed.Where(p => p.GetProperty("merged_at").ValueKind == JsonValueKind.String).ToList();
        var openToMerge = merged.Select(p => Date(p, "merged_at") - Date(p, "created_at")).ToList();
        var agentMerged = merged.Where(p => Login(p) == options.AgentLogin).Take(20).ToList();
        var clean = 0;
        foreach (var p in agentMerged)
        {
            if (!await NeededChangesAsync(p, ct))
            {
                clean++;
            }
        }

        // Size, merge queue wait and human review time, from each recent merged PR and its timeline.
        var facts = new List<PullFacts>();
        foreach (var p in merged.Take(30))
        {
            if (await PullFactsAsync(p, ct) is { } f)
            {
                facts.Add(f);
            }
        }

        var deploys = await WorkflowRunsAsync(options.DeployWorkflow, 30, ct);
        var rollbacks = 0;
        foreach (var run in deploys.Where(r => r.Conclusion == "failure" && r.Id != 0 && r.StartedAt >= now.AddDays(-30)))
        {
            if (await ProdRolledBackAsync(run, ct))
            {
                rollbacks++;
            }
        }

        // Production deploys in the last 30 days: the deploy runs whose Production job ran.
        var mergedBySha = merged
            .Where(p => p.TryGetProperty("merge_commit_sha", out var sha) && sha.ValueKind == JsonValueKind.String)
            .GroupBy(p => p.GetProperty("merge_commit_sha").GetString()!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Date(g.First(), "merged_at"), StringComparer.Ordinal);
        int prodDeploys = 0, prodFailures = 0;
        var mergeToProd = new List<TimeSpan>();
        foreach (var run in deploys.Where(r => r.Status == "completed" && r.StartedAt >= now.AddDays(-30)))
        {
            var prod = ProductionJob(await RunJobsAsync(run, ct));
            if (prod is null)
            {
                continue;
            }

            prodDeploys++;
            if (prod.Conclusion != "success" || run.Title.StartsWith("Revert ", StringComparison.Ordinal))
            {
                prodFailures++;
            }

            if (prod.Conclusion == "success" && run.HeadSha is { } head && mergedBySha.TryGetValue(head, out var mergedAt))
            {
                mergeToProd.Add((prod.CompletedAt ?? run.UpdatedAt) - mergedAt);
            }
        }

        var stats = new DeliveryStats(ci, openToMerge, agentMerged.Count, clean, deploys, rollbacks)
        {
            CiJobDurations = jobDurations.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TimeSpan>)kv.Value, StringComparer.Ordinal),
            PullSizes = facts.Select(f => f.ChangedLines).OfType<int>().ToList(),
            MergeQueueWaits = facts.Select(f => f.QueueWait).OfType<TimeSpan>().ToList(),
            HumanReviewTimes = facts.Select(f => f.HumanReview).OfType<TimeSpan>().ToList(),
            MergeToProduction = mergeToProd,
            ProdDeploys = prodDeploys,
            ProdDeployFailures = prodFailures,
            FlakyTests = await CountOrNullAsync($"{_repo}/issues?state=open&labels={FlakyLabel}&per_page=100", ct),
            MutationScore = await MutationScoreAsync(ct),
        };
        _stats = (now + TimeSpan.FromMinutes(5), stats);
        return stats;
    }

    public async Task<PullReviewState> GetReviewStateAsync(int number, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_reviewStates.TryGetValue(number, out var hit) && hit.Until > now)
        {
            return hit.State;
        }

        var parts = options.GitHubRepository.Split('/');
        var json = await SendAsync(HttpMethod.Post, "graphql", new
        {
            query = "query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { reviewDecision mergeStateStatus } } }",
            variables = new { owner = parts[0], name = parts.Length > 1 ? parts[1] : "", number },
        }, ct);
        var pr = json.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
        string? Text(string name) => pr.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var state = new PullReviewState(number, Text("reviewDecision"), Text("mergeStateStatus"), await TouchesCodeOwnedAsync(number, ct));
        _reviewStates[number] = (now + TimeSpan.FromSeconds(15), state);
        return state;
    }

    private async Task<bool> TouchesCodeOwnedAsync(int number, CancellationToken ct)
    {
        var owners = await GetAsync($"{_repo}/contents/.github/CODEOWNERS", ct);
        var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(owners.GetProperty("content").GetString()!.Replace("\n", "")));
        var patterns = CodeOwners.Parse(text);
        var files = (await GetArrayAsync($"{_repo}/pulls/{number}/files?per_page=100", ct)).Select(f => f.GetProperty("filename").GetString()!);
        return files.Any(f => CodeOwners.Matches(patterns, f));
    }

    private async Task<int?> CountOrNullAsync(string path, CancellationToken ct)
    {
        try
        {
            return (await GetArrayAsync(path, ct)).Count(i => !i.TryGetProperty("pull_request", out _));
        }
        catch (GitHubException)
        {
            return null;
        }
    }

    /// <summary>The latest successful nightly mutation run's score: its <c>::notice title=mutation-score::&lt;pct&gt;</c> annotation.</summary>
    private async Task<double?> MutationScoreAsync(CancellationToken ct)
    {
        var latest = await WorkflowRunsAsync(MutationWorkflow, 1, ct, "&status=success");
        if (latest is not [var run, ..] || run.Id == 0)
        {
            return null;
        }

        if (_mutationScores.TryGetValue(run.Id, out var known))
        {
            return known;
        }

        double? score = null;
        foreach (var job in await RunJobsAsync(run, ct) ?? [])
        {
            var annotations = await GetArrayAsync($"{_repo}/check-runs/{job.Id.ToString(CultureInfo.InvariantCulture)}/annotations?per_page=50", ct);
            foreach (var a in annotations)
            {
                if (a.TryGetProperty("title", out var title) && title.GetString() == MutationScoreTitle
                    && a.TryGetProperty("message", out var message)
                    && double.TryParse((message.GetString() ?? "").Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                {
                    score = pct;
                }
            }
        }

        _mutationScores[run.Id] = score;
        return score;
    }

    /// <summary>A run's jobs; kept for good once the run is completed. Null when GitHub could not say.</summary>
    private async Task<IReadOnlyList<JobInfo>?> RunJobsAsync(WorkflowRunSummary run, CancellationToken ct)
    {
        var key = (run.Id, run.UpdatedAt);
        if (run.Id == 0)
        {
            return null;
        }

        if (_jobs.TryGetValue(key, out var known))
        {
            return known;
        }

        JsonElement json;
        try
        {
            json = await GetAsync($"{_repo}/actions/runs/{run.Id.ToString(CultureInfo.InvariantCulture)}/jobs?per_page=100", ct);
        }
        catch (GitHubException)
        {
            return null;
        }

        static DateTimeOffset? At(JsonElement j, string name) =>
            j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;
        var jobs = json.GetProperty("jobs").EnumerateArray().Select(j => new JobInfo(
            j.GetProperty("id").GetInt64(),
            j.GetProperty("name").GetString() ?? "",
            j.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
            j.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
            At(j, "started_at"),
            At(j, "completed_at"))).ToList();
        if (run.Status == "completed")
        {
            _jobs[key] = jobs;
        }

        return jobs;
    }

    /// <summary>The deploy run's Production job (reusable workflow jobs are named "Production / ..."), if it ran.</summary>
    internal static JobInfo? ProductionJob(IReadOnlyList<JobInfo>? jobs) => EnvironmentJob(jobs, ProdJobPrefix);

    private static JobInfo? EnvironmentJob(IReadOnlyList<JobInfo>? jobs, string prefix)
    {
        var matching = (jobs ?? []).Where(j => j.Name.StartsWith(prefix, StringComparison.Ordinal) && j.Conclusion is not "skipped" && j.StartedAt is not null).ToList();
        if (matching.Count == 0)
        {
            return null;
        }

        var done = matching.All(j => j.Status == "completed");
        var conclusion = !done ? null : matching.All(j => j.Conclusion == "success") ? "success" : "failure";
        return new JobInfo(matching[0].Id, prefix, done ? "completed" : "in_progress", conclusion,
            matching.Min(j => j.StartedAt), done ? matching.Max(j => j.CompletedAt) : null);
    }

    /// <summary>What the scoreboard needs from one merged PR: its size, merge queue wait and human review time.</summary>
    private async Task<PullFacts?> PullFactsAsync(JsonElement pull, CancellationToken ct)
    {
        var number = pull.GetProperty("number").GetInt32();
        if (_pullFacts.TryGetValue(number, out var known))
        {
            return known;
        }

        try
        {
            var detail = await GetAsync($"{_repo}/pulls/{number}", ct);
            int? size = detail.TryGetProperty("additions", out var add) && detail.TryGetProperty("deletions", out var del)
                ? add.GetInt32() + del.GetInt32()
                : null;
            var timeline = await GetArrayAsync($"{_repo}/issues/{number}/timeline?per_page=100", ct);
            var facts = Timeline.Facts(timeline, Date(pull, "created_at"), Date(pull, "merged_at"), Login(pull)) with { ChangedLines = size };
            _pullFacts[number] = facts;
            return facts;
        }
        catch (GitHubException)
        {
            return null;
        }
    }

    /// <summary>The AI review blocked the PR (it labels it <c>ai-review:blocked</c>, and the label stays after the fix),
    /// or a person's review asked for changes.</summary>
    private async Task<bool> NeededChangesAsync(JsonElement pull, CancellationToken ct)
    {
        if (pull.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
            && labels.EnumerateArray().Any(l => l.GetProperty("name").GetString() == AiReviewBlockedLabel))
        {
            return true;
        }

        var reviews = await GetArrayAsync($"{_repo}/pulls/{pull.GetProperty("number").GetInt32()}/reviews?per_page=100", ct);
        return reviews.Any(r => r.GetProperty("state").GetString() == "CHANGES_REQUESTED");
    }

    /// <summary>A failed deploy run whose Production job reported that ECS rolled the service back
    /// (deploy-service.yml's "::error::Production rolled back to the previous version" annotation).
    /// Completed runs don't change until re-run, so the answer is cached per run attempt.</summary>
    private async Task<bool> ProdRolledBackAsync(WorkflowRunSummary run, CancellationToken ct)
    {
        var key = (run.Id, run.UpdatedAt);
        if (_rollbacks.TryGetValue(key, out var known))
        {
            return known;
        }

        var jobs = await RunJobsAsync(run, ct);
        if (jobs is null)
        {
            return false;
        }

        var rolledBack = false;
        foreach (var job in jobs.Where(j => j.Name.StartsWith(ProdJobPrefix, StringComparison.Ordinal) && j.Conclusion == "failure"))
        {
            var annotations = await GetArrayAsync($"{_repo}/check-runs/{job.Id.ToString(CultureInfo.InvariantCulture)}/annotations?per_page=50", ct);
            if (annotations.Any(a => a.TryGetProperty("message", out var m) && (m.GetString() ?? "").Contains(ProdRollbackMessage, StringComparison.Ordinal)))
            {
                rolledBack = true;
                break;
            }
        }

        _rollbacks[key] = rolledBack;
        return rolledBack;
    }

    public async Task<IReadOnlyList<ConsoleEvent>> PollAsync(DateTimeOffset since, CancellationToken ct)
    {
        var events = new List<ConsoleEvent>();
        void Add(DateTimeOffset at, string kind, string subject, string? detail, string? url)
        {
            if (at >= since)
            {
                events.Add(new ConsoleEvent(at, kind, subject, detail, url));
            }
        }

        foreach (var e in await GetArrayAsync($"{_repo}/issues/events?per_page=100", ct))
        {
            if (!e.TryGetProperty("issue", out var issue) || issue.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Pull requests are issues too; their label events get a pr#N subject (e.g. needs-human after a second AI block).
            var number = issue.GetProperty("number").GetInt32();
            var isPull = issue.TryGetProperty("pull_request", out _);
            if (isPull && e.GetProperty("event").GetString() != "labeled")
            {
                continue;
            }

            var subject = isPull ? ConsoleEvent.PullSubject(number) : ConsoleEvent.IssueSubject(number);
            var url = issue.GetProperty("html_url").GetString();
            switch (e.GetProperty("event").GetString())
            {
                case "labeled" when e.TryGetProperty("label", out var label):
                    Add(Date(e, "created_at"), EventKinds.IssueLabeled, subject, label.GetProperty("name").GetString(), url);
                    break;
                case "assigned" when e.TryGetProperty("assignee", out var assignee) && assignee.ValueKind == JsonValueKind.Object:
                    Add(Date(e, "created_at"), EventKinds.IssueAssigned, subject, assignee.GetProperty("login").GetString(), url);
                    break;
            }
        }

        foreach (var i in await GetArrayAsync($"{_repo}/issues?state=all&sort=created&direction=desc&per_page=30", ct))
        {
            if (!i.TryGetProperty("pull_request", out _))
            {
                Add(Date(i, "created_at"), EventKinds.IssueOpened, ConsoleEvent.IssueSubject(i.GetProperty("number").GetInt32()),
                    $"{Login(i)}: {i.GetProperty("title").GetString()}", i.GetProperty("html_url").GetString());
            }
        }

        var pulls = await GetArrayAsync($"{_repo}/pulls?state=all&sort=updated&direction=desc&per_page=30", ct);
        foreach (var p in pulls)
        {
            var number = p.GetProperty("number").GetInt32();
            var subject = ConsoleEvent.PullSubject(number);
            var url = p.GetProperty("html_url").GetString();
            var refs = References(p);
            var refText = refs.Count == 0 ? "" : $" (refs {string.Join(", ", refs.Select(r => "#" + r.ToString(CultureInfo.InvariantCulture)))})";
            Add(Date(p, "created_at"), EventKinds.PrOpened, subject, $"{Login(p)}: {p.GetProperty("title").GetString()}{refText}", url);
            if (p.GetProperty("merged_at").ValueKind == JsonValueKind.String)
            {
                Add(Date(p, "merged_at"), EventKinds.PrMerged, subject, $"merged into {p.GetProperty("base").GetProperty("ref").GetString()}", url);
            }
        }

        // Check results for the most recently updated open PRs.
        foreach (var p in pulls.Where(p => p.GetProperty("state").GetString() == "open").Take(5))
        {
            var subject = ConsoleEvent.PullSubject(p.GetProperty("number").GetInt32());
            var sha = p.GetProperty("head").GetProperty("sha").GetString();
            var checks = await GetAsync($"{_repo}/commits/{sha}/check-runs?per_page=100", ct);
            foreach (var c in checks.GetProperty("check_runs").EnumerateArray())
            {
                if (c.GetProperty("status").GetString() != "completed" || c.GetProperty("completed_at").ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = c.GetProperty("name").GetString() ?? "check";
                var conclusion = c.GetProperty("conclusion").GetString();
                var at = Date(c, "completed_at");
                Add(at, EventKinds.CheckCompleted, subject, $"{name}: {conclusion}", c.GetProperty("html_url").GetString());
                if (conclusion == "success" && name.Contains("preview", StringComparison.OrdinalIgnoreCase))
                {
                    Add(at, EventKinds.PreviewUp, subject, $"pr-{subject[3..]}.demo.joshb.app", $"https://pr-{subject[3..]}.demo.joshb.app");
                }
            }
        }

        // Deploys: one deploy run goes to staging, then production; each environment's job is its own event.
        foreach (var run in (await WorkflowRunsAsync(options.DeployWorkflow, 10, ct)).Where(r => r.UpdatedAt >= since).Take(5))
        {
            var detail = $"{run.Branch}: {run.Title}";
            var jobs = await RunJobsAsync(run, ct);
            foreach (var (prefix, environment) in new[] { (StagingJobPrefix, "staging"), (ProdJobPrefix, "prod") })
            {
                if (EnvironmentJob(jobs, prefix) is not { StartedAt: { } started } job)
                {
                    continue;
                }

                Add(started, EventKinds.DeployStarted, environment, detail, run.Url);
                if (job is { Conclusion: "success", CompletedAt: { } done })
                {
                    Add(done, EventKinds.DeploySucceeded, environment, detail, run.Url);
                }
            }
        }

        return events;
    }

    private async Task<IReadOnlyList<WorkflowRunSummary>> WorkflowRunsAsync(string workflowFile, int count, CancellationToken ct, string extra = "")
    {
        JsonElement json;
        try
        {
            json = await GetAsync($"{_repo}/actions/workflows/{workflowFile}/runs?per_page={count}{extra}", ct);
        }
        catch (GitHubException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return [];
        }

        var name = Path.GetFileNameWithoutExtension(workflowFile);
        return json.GetProperty("workflow_runs").EnumerateArray().Select(r => new WorkflowRunSummary(
            name,
            r.GetProperty("status").GetString() ?? "unknown",
            r.GetProperty("conclusion").ValueKind == JsonValueKind.String ? r.GetProperty("conclusion").GetString() : null,
            r.GetProperty("display_title").GetString() ?? name,
            r.GetProperty("head_branch").GetString() ?? "",
            r.GetProperty("html_url").GetString() ?? "",
            r.TryGetProperty("run_started_at", out var started) && started.ValueKind == JsonValueKind.String ? started.GetDateTimeOffset() : Date(r, "created_at"),
            Date(r, "updated_at"),
            r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
            r.TryGetProperty("head_sha", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null,
            r.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString() : null)).ToList();
    }

    private static IssueSummary ToIssue(JsonElement i) => new(
        i.GetProperty("number").GetInt32(),
        i.GetProperty("title").GetString() ?? "",
        i.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()!).ToList(),
        Login(i),
        i.GetProperty("html_url").GetString() ?? "",
        Date(i, "created_at"),
        i.TryGetProperty("state", out var st) ? st.GetString() ?? "open" : "open");

    private static PullRequestSummary ToPull(JsonElement p)
    {
        var merged = p.GetProperty("merged_at").ValueKind == JsonValueKind.String ? Date(p, "merged_at") : (DateTimeOffset?)null;
        var state = merged is not null ? "merged" : p.GetProperty("state").GetString() ?? "open";
        return new PullRequestSummary(p.GetProperty("number").GetInt32(), p.GetProperty("title").GetString() ?? "", Login(p), state,
            p.GetProperty("html_url").GetString() ?? "", Date(p, "created_at"), merged);
    }

    private static string Login(JsonElement e) =>
        e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u.GetProperty("login").GetString() ?? "" : "";

    private static DateTimeOffset Date(JsonElement e, string name) => e.GetProperty(name).GetDateTimeOffset();

    private static List<int> References(JsonElement pr)
    {
        var text = (pr.GetProperty("title").GetString() ?? "") + "\n" + (pr.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "");
        return IssueRef().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().ToList();
    }

    [GeneratedRegex(@"#(\d+)\b")]
    private static partial Regex IssueRef();

    private async Task<List<JsonElement>> GetArrayAsync(string path, CancellationToken ct) =>
        (await GetAsync(path, ct)).EnumerateArray().ToList();

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path.TrimStart('/'), ct);
        return await ReadAsync(response, ct);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/')) { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request, ct);
        var json = await ReadAsync(response, ct);
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 && path == "graphql")
        {
            throw new GitHubException(HttpStatusCode.UnprocessableEntity, errors[0].GetProperty("message").GetString() ?? "GraphQL error");
        }

        return json;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = text;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("message", out var m))
                {
                    message = m.GetString() ?? text;
                }
            }
            catch (JsonException)
            {
                // Not JSON; use the raw text.
            }

            throw new GitHubException(response.StatusCode, $"GitHub {(int)response.StatusCode}: {message}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not turn on auto-merge for PR #{Number}")]
    private partial void LogAutoMergeFailed(Exception ex, int number);
}

/// <summary>One job of a workflow run.</summary>
public sealed record JobInfo(long Id, string Name, string Status, string? Conclusion, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
{
    public TimeSpan? Duration => StartedAt is { } s && CompletedAt is { } c && c >= s ? c - s : null;
}

/// <summary>Scoreboard facts about one merged PR.</summary>
public sealed record PullFacts(int? ChangedLines, TimeSpan? QueueWait, TimeSpan? HumanReview);

/// <summary>Reads a PR's timeline (GET /issues/{n}/timeline).</summary>
public static class Timeline
{
    /// <summary>
    /// Merge queue wait: the last <c>added_to_merge_queue</c> before the merge, to the merge.
    /// Human review time, on PRs that needed one (a person was asked to review, or reviewed): from the review
    /// request or ready-for-review, whichever came last before the first review by a person other than the author,
    /// to that review. Bots (the AI review, Dependabot) don't count as people.
    /// </summary>
    public static PullFacts Facts(IReadOnlyList<JsonElement> timeline, DateTimeOffset createdAt, DateTimeOffset mergedAt, string author)
    {
        static DateTimeOffset? At(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;
        static string Event(JsonElement e) => e.TryGetProperty("event", out var v) ? v.GetString() ?? "" : "";
        static bool Human(JsonElement e, string property, out string login)
        {
            login = "";
            if (!e.TryGetProperty(property, out var user) || user.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            login = user.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
            var type = user.TryGetProperty("type", out var t) ? t.GetString() : null;
            return type != "Bot" && !login.EndsWith("[bot]", StringComparison.Ordinal);
        }

        var queued = timeline.Where(e => Event(e) == "added_to_merge_queue").Select(e => At(e, "created_at")).OfType<DateTimeOffset>()
            .Where(t => t <= mergedAt).DefaultIfEmpty().Max();
        TimeSpan? queueWait = queued == default ? null : mergedAt - queued;

        var requests = timeline.Where(e => Event(e) == "review_requested" && Human(e, "requested_reviewer", out _))
            .Select(e => At(e, "created_at")).OfType<DateTimeOffset>().ToList();
        var ready = timeline.Where(e => Event(e) == "ready_for_review").Select(e => At(e, "created_at")).OfType<DateTimeOffset>().ToList();
        var reviews = timeline.Where(e => Event(e) == "reviewed" && Human(e, "user", out var login) && login != author
                && e.TryGetProperty("state", out var st) && st.GetString() is "approved" or "changes_requested" or "commented")
            .Select(e => At(e, "submitted_at")).OfType<DateTimeOffset>().Order().ToList();

        TimeSpan? review = null;
        if (reviews.Count > 0)
        {
            var first = reviews[0];
            var start = requests.Concat(ready).Where(t => t <= first).DefaultIfEmpty(createdAt).Max();
            review = first - start;
        }

        return new PullFacts(null, queueWait, review);
    }
}

public sealed class GitHubException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
