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
        var runs = new List<WorkflowRunSummary>();
        foreach (var workflow in options.Workflows)
        {
            var latest = await WorkflowRunsAsync(workflow, 1, ct);
            runs.AddRange(latest);
        }

        return new GitHubActivity(labels, issues.Count, agentPulls, runs);
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

        var ci = (await WorkflowRunsAsync(options.Workflows.Count > 0 ? options.Workflows[0] : "ci.yml", 50, ct, "&status=completed"))
            .Select(r => r.Duration).OfType<TimeSpan>().ToList();
        var closed = await GetArrayAsync($"{_repo}/pulls?state=closed&sort=updated&direction=desc&per_page=50", ct);
        var merged = closed.Where(p => p.GetProperty("merged_at").ValueKind == JsonValueKind.String).ToList();
        var openToMerge = merged.Select(p => Date(p, "merged_at") - Date(p, "created_at")).ToList();
        var agentMerged = merged.Where(p => Login(p) == options.AgentLogin).Take(20).ToList();
        var clean = 0;
        foreach (var p in agentMerged)
        {
            var reviews = await GetArrayAsync($"{_repo}/pulls/{p.GetProperty("number").GetInt32()}/reviews?per_page=100", ct);
            if (!reviews.Any(r => r.GetProperty("state").GetString() == "CHANGES_REQUESTED"))
            {
                clean++;
            }
        }

        var deploys = await WorkflowRunsAsync(options.DeployWorkflow, 30, ct);
        var stats = new DeliveryStats(ci, openToMerge, agentMerged.Count, clean, deploys);
        _stats = (now + TimeSpan.FromMinutes(5), stats);
        return stats;
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

        foreach (var run in await WorkflowRunsAsync(options.DeployWorkflow, 10, ct))
        {
            var detail = $"{run.Branch}: {run.Title}";
            Add(run.StartedAt, EventKinds.DeployStarted, "prod", detail, run.Url);
            if (run.Status == "completed" && run.Conclusion == "success")
            {
                Add(run.UpdatedAt, EventKinds.DeploySucceeded, "prod", detail, run.Url);
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
            Date(r, "updated_at"))).ToList();
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

public sealed class GitHubException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
