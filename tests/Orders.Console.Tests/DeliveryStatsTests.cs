using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orders.Console.Integrations.Fakes;
using Orders.Console.Integrations.GitHub;
using Orders.Console.Status;

namespace Orders.Console.Tests;

/// <summary>Scoreboard numbers from GitHub API responses recorded in the shape GitHub returns them.</summary>
public sealed class DeliveryStatsTests
{
    private const string Repo = "/repos/billigmeier-labs/orders-demo";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Pull(int number, string author, string? mergedAt, params string[] labels) => $$"""
        {
          "number": {{number}}, "title": "PR {{number}}", "state": "closed",
          "user": { "login": "{{author}}" },
          "created_at": "2026-11-08T10:00:00Z",
          "merged_at": {{(mergedAt is null ? "null" : $"\"{mergedAt}\"")}},
          "labels": [{{string.Join(",", labels.Select(l => $$"""{ "name": "{{l}}" }"""))}}]
        }
        """;

    private static string Run(long id, string conclusion, string startedAt) => $$"""
        {
          "id": {{id}}, "status": "completed", "conclusion": "{{conclusion}}", "display_title": "Deploy {{id}}",
          "head_branch": "main", "html_url": "https://github.com/billigmeier-labs/orders-demo/actions/runs/{{id}}",
          "created_at": "{{startedAt}}", "run_started_at": "{{startedAt}}", "updated_at": "{{startedAt}}"
        }
        """;

    private static string Job(long id, string name, string conclusion) =>
        $$"""{ "id": {{id}}, "name": "{{name}}", "status": "completed", "conclusion": "{{conclusion}}" }""";

    private static string TimedJob(long id, string name, string? conclusion, string startedAt, string? completedAt) =>
        $$"""{ "id": {{id}}, "name": "{{name}}", "status": "{{(completedAt is null ? "in_progress" : "completed")}}", "conclusion": {{(conclusion is null ? "null" : $"\"{conclusion}\"")}}, "started_at": "{{startedAt}}", "completed_at": {{(completedAt is null ? "null" : $"\"{completedAt}\"")}} }""";

    private static string ShaRun(long id, string conclusion, string startedAt, string updatedAt, string title, string sha, string ev = "push", string status = "completed") => $$"""
        {
          "id": {{id}}, "status": "{{status}}", "conclusion": {{(status == "completed" ? $"\"{conclusion}\"" : "null")}}, "display_title": "{{title}}",
          "head_branch": "main", "head_sha": "{{sha}}", "event": "{{ev}}", "html_url": "https://github.com/billigmeier-labs/orders-demo/actions/runs/{{id}}",
          "created_at": "{{startedAt}}", "run_started_at": "{{startedAt}}", "updated_at": "{{updatedAt}}"
        }
        """;

    private static string Annotation(string message) =>
        $$"""{ "path": ".github", "annotation_level": "failure", "message": "{{message}}" }""";

    private static StubGitHub Recorded()
    {
        var gh = new StubGitHub();
        gh.Routes[$"{Repo}/actions/workflows/ci.yml/runs"] = """{ "workflow_runs": [] }""";
        gh.Routes[$"{Repo}/pulls"] = "[" + string.Join(",",
            Pull(10, "claude[bot]", "2026-11-08T11:00:00Z"),                                   // clean
            Pull(11, "claude[bot]", "2026-11-08T11:00:00Z", "ai-review:blocked"),              // AI review blocked once, then fixed
            Pull(12, "claude[bot]", "2026-11-08T11:00:00Z", "ai-review:blocked", "needs-human"), // blocked twice
            Pull(13, "claude[bot]", "2026-11-08T11:00:00Z"),                                   // a person asked for changes
            Pull(14, "jbilligmeier", "2026-11-08T11:00:00Z", "ai-review:blocked"),             // not an agent PR
            Pull(15, "claude[bot]", null)) + "]";                                              // closed, not merged
        gh.Routes[$"{Repo}/pulls/10/reviews"] = """[{ "state": "APPROVED" }, { "state": "COMMENTED" }]""";
        gh.Routes[$"{Repo}/pulls/13/reviews"] = """[{ "state": "CHANGES_REQUESTED" }, { "state": "APPROVED" }]""";
        gh.Routes[$"{Repo}/actions/workflows/deploy.yml/runs"] = """{ "workflow_runs": [""" + string.Join(",",
            Run(901, "success", "2026-11-09T10:00:00Z"),
            Run(902, "failure", "2026-11-08T10:00:00Z"),  // prod rolled back
            Run(903, "failure", "2026-11-07T10:00:00Z"),  // staging rolled back; prod never ran
            Run(904, "failure", "2026-11-06T10:00:00Z"),  // prod smoke test failed, no rollback
            Run(905, "failure", "2026-09-01T10:00:00Z")) + "] }"; // prod rolled back, but over 30 days ago
        gh.Routes[$"{Repo}/actions/runs/901/jobs"] = """{ "jobs": [""" + string.Join(",",
            Job(11, "Prepare", "success"), Job(12, "Staging / Deploy", "success"), Job(13, "Production / Deploy", "success")) + "] }";
        gh.Routes[$"{Repo}/actions/runs/902/jobs"] = """{ "jobs": [""" + string.Join(",",
            Job(1, "Prepare", "success"), Job(2, "Staging / Deploy", "success"), Job(3, "Production / Deploy", "failure"), Job(4, "Console / Deploy", "skipped")) + "] }";
        gh.Routes[$"{Repo}/check-runs/3/annotations"] = "[" + string.Join(",",
            Annotation("Production rolled back to the previous version"), Annotation("Process completed with exit code 1.")) + "]";
        gh.Routes[$"{Repo}/actions/runs/903/jobs"] = """{ "jobs": [""" + string.Join(",",
            Job(5, "Prepare", "success"), Job(6, "Staging / Deploy", "failure"), Job(7, "Production / Deploy", "skipped")) + "] }";
        gh.Routes[$"{Repo}/actions/runs/904/jobs"] = """{ "jobs": [""" + string.Join(",",
            Job(8, "Prepare", "success"), Job(9, "Staging / Deploy", "success"), Job(10, "Production / Deploy", "failure")) + "] }";
        gh.Routes[$"{Repo}/check-runs/10/annotations"] = "[" + Annotation("https://demo.joshb.app/api/version does not report commit abc1234") + "]";
        gh.Routes[$"{Repo}/actions/runs/905/jobs"] = gh.Routes[$"{Repo}/actions/runs/902/jobs"];
        return gh;
    }

    private static (GitHubService Service, FakeTimeProvider Time) Service(StubGitHub gh)
    {
        var options = new ConsoleOptions();
        var http = new HttpClient(gh);
        GitHubService.Configure(http, options);
        var time = new FakeTimeProvider(Now);
        return (new GitHubService(http, options, time, NullLogger<GitHubService>.Instance), time);
    }

    [Fact]
    public async Task Agent_prs_count_as_needing_changes_when_the_ai_review_blocked_them_or_a_review_asked_for_changes()
    {
        var gh = Recorded();
        var stats = await Service(gh).Service.GetDeliveryStatsAsync(Ct);

        Assert.Equal(4, stats.AgentPullsMerged);
        Assert.Equal(1, stats.AgentPullsMergedWithoutBlock);
        // The label answers it for blocked PRs; reviews are fetched only for the others.
        Assert.DoesNotContain(gh.Requests, r => r.Contains("/pulls/11/reviews", StringComparison.Ordinal) || r.Contains("/pulls/12/reviews", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rollbacks_count_only_prod_deploys_ecs_rolled_back_in_the_last_30_days()
    {
        var stats = await Service(Recorded()).Service.GetDeliveryStatsAsync(Ct);

        Assert.Equal(1, stats.ProdRollbacks);
        Assert.Equal(5, stats.DeployRuns.Count);
    }

    [Fact]
    public async Task Rollback_checks_are_cached_per_run()
    {
        var gh = Recorded();
        var (service, time) = Service(gh);
        await service.GetDeliveryStatsAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(6)); // past the stats cache
        gh.Requests.Clear();

        var stats = await service.GetDeliveryStatsAsync(Ct);

        Assert.Equal(1, stats.ProdRollbacks);
        Assert.DoesNotContain(gh.Requests, r => r.Contains("/jobs", StringComparison.Ordinal) || r.Contains("/annotations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scoreboard_rows_say_what_they_measure()
    {
        var gh = Recorded();
        var world = new FakeWorld(new FakeTimeProvider(Now), new ConsoleOptions());
        var board = await new ScoreboardService(Service(gh).Service, world, world, new ConsoleOptions(), new FakeTimeProvider(Now)).GetAsync(Ct);

        var clean = Assert.Single(board.Rows, r => r.Id == "agent-clean");
        Assert.Equal("Agent PRs merged without an AI-review block", clean.Name);
        Assert.Equal("1 of 4 (25%)", clean.Value);
        Assert.Equal("Over 70%", clean.Target);
        Assert.False(clean.Pass);
        var rollbacks = Assert.Single(board.Rows, r => r.Id == "rollbacks");
        Assert.Equal("Prod rollbacks, last 30 days", rollbacks.Name);
        Assert.Equal("1", rollbacks.Value);
    }

    [Fact]
    public async Task Scoreboard_metrics_come_from_jobs_timelines_deploys_and_annotations()
    {
        const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var gh = new StubGitHub();
        gh.Routes[$"{Repo}/actions/workflows/ci.yml/runs"] = """{ "workflow_runs": [""" + string.Join(",",
            ShaRun(801, "success", "2026-11-08T10:00:00Z", "2026-11-08T10:04:00Z", "PR 20", "b1", "pull_request"),
            ShaRun(802, "success", "2026-11-08T10:30:00Z", "2026-11-08T10:35:00Z", "PR 20", "b2", "merge_group"),
            ShaRun(803, "success", "2026-11-08T11:00:00Z", "2026-11-08T11:20:00Z", "main", "b3", "push")) + "] }";
        gh.Routes[$"{Repo}/actions/runs/801/jobs"] = """{ "jobs": [""" + string.Join(",",
            TimedJob(1, "Integration tests", "success", "2026-11-08T10:00:00Z", "2026-11-08T10:03:00Z"),
            TimedJob(2, "PR rules", "success", "2026-11-08T10:00:00Z", "2026-11-08T10:00:20Z"),
            TimedJob(3, "Release image", "skipped", "2026-11-08T10:00:00Z", "2026-11-08T10:00:00Z")) + "] }";
        gh.Routes[$"{Repo}/actions/runs/802/jobs"] = """{ "jobs": [""" +
            TimedJob(4, "Integration tests", "success", "2026-11-08T10:30:00Z", "2026-11-08T10:34:00Z") + "] }";
        gh.Routes[$"{Repo}/pulls"] = """
            [{ "number": 20, "title": "feat: x (#20)", "state": "closed", "user": { "login": "claude[bot]" },
               "created_at": "2026-11-08T10:00:00Z", "merged_at": "2026-11-08T11:00:00Z", "merge_commit_sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "labels": [] }]
            """;
        gh.Routes[$"{Repo}/pulls/20/reviews"] = "[]";
        gh.Routes[$"{Repo}/pulls/20"] = """{ "number": 20, "additions": 120, "deletions": 30 }""";
        gh.Routes[$"{Repo}/issues/20/timeline"] = """
            [
              { "event": "reviewed", "state": "commented", "submitted_at": "2026-11-08T10:03:00Z", "user": { "login": "github-actions[bot]", "type": "Bot" } },
              { "event": "review_requested", "created_at": "2026-11-08T10:05:00Z", "requested_reviewer": { "login": "jbilligmeier", "type": "User" } },
              { "event": "reviewed", "state": "approved", "submitted_at": "2026-11-08T10:12:00Z", "user": { "login": "jbilligmeier", "type": "User" } },
              { "event": "added_to_merge_queue", "created_at": "2026-11-08T10:50:00Z" },
              { "event": "merged", "created_at": "2026-11-08T11:00:00Z" }
            ]
            """;
        gh.Routes[$"{Repo}/actions/workflows/deploy.yml/runs"] = """{ "workflow_runs": [""" + string.Join(",",
            ShaRun(910, "success", "2026-11-08T11:01:00Z", "2026-11-08T11:10:00Z", "feat: x (#20)", Sha),
            ShaRun(911, "success", "2026-11-08T12:00:00Z", "2026-11-08T12:10:00Z", "Revert \\\"feat: x (#20)\\\"", "c1")) + "] }";
        gh.Routes[$"{Repo}/actions/runs/910/jobs"] = """{ "jobs": [""" + string.Join(",",
            TimedJob(10, "Staging / Deploy", "success", "2026-11-08T11:01:00Z", "2026-11-08T11:04:00Z"),
            TimedJob(11, "Production / Deploy", "success", "2026-11-08T11:05:00Z", "2026-11-08T11:09:00Z")) + "] }";
        gh.Routes[$"{Repo}/actions/runs/911/jobs"] = """{ "jobs": [""" +
            TimedJob(12, "Production / Deploy", "success", "2026-11-08T12:05:00Z", "2026-11-08T12:09:00Z") + "] }";
        gh.Routes[$"{Repo}/issues"] = """[{ "number": 30, "title": "Flaky: A" }, { "number": 31, "title": "Flaky: B" }, { "number": 32, "pull_request": {} }]""";
        gh.Routes[$"{Repo}/actions/workflows/mutation.yml/runs"] = """{ "workflow_runs": [""" + ShaRun(950, "success", "2026-11-08T03:00:00Z", "2026-11-08T03:40:00Z", "Mutation", "d1", "schedule") + "] }";
        gh.Routes[$"{Repo}/actions/runs/950/jobs"] = """{ "jobs": [""" + TimedJob(51, "Stryker", "success", "2026-11-08T03:00:00Z", "2026-11-08T03:39:00Z") + "] }";
        gh.Routes[$"{Repo}/check-runs/51/annotations"] = """[{ "annotation_level": "notice", "title": "mutation-score", "message": "72.5" }, { "annotation_level": "notice", "title": "other", "message": "1" }]""";

        var stats = await Service(gh).Service.GetDeliveryStatsAsync(Ct);

        Assert.Equal([TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5)], stats.CiDurations);
        Assert.Equal([TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(4)], stats.CiJobDurations["Integration tests"]);
        Assert.Equal([TimeSpan.FromSeconds(20)], stats.CiJobDurations["PR rules"]);
        Assert.False(stats.CiJobDurations.ContainsKey("Release image"));
        Assert.Equal([150], stats.PullSizes);
        Assert.Equal([TimeSpan.FromMinutes(7)], stats.HumanReviewTimes);
        Assert.Equal([TimeSpan.FromMinutes(10)], stats.MergeQueueWaits);
        Assert.Equal([TimeSpan.FromMinutes(9)], stats.MergeToProduction);
        Assert.Equal((2, 1), (stats.ProdDeploys, stats.ProdDeployFailures));
        Assert.Equal(2, stats.FlakyTests);
        Assert.Equal(72.5, stats.MutationScore);

        var world = new FakeWorld(new FakeTimeProvider(Now), new ConsoleOptions());
        var board = await new ScoreboardService(Service(gh).Service, world, world, new ConsoleOptions(), new FakeTimeProvider(Now)).GetAsync(Ct);
        Assert.Equal(("9 min 0 s", true), (board.Row("merge-to-prod")!.Value, board.Row("merge-to-prod")!.Pass));
        Assert.Equal(("1 of 2 (50%)", false), (board.Row("change-failure")!.Value, board.Row("change-failure")!.Pass));
        Assert.Equal(("72.5%", true), (board.Row("mutation")!.Value, board.Row("mutation")!.Pass));
        Assert.Equal(("150 lines", true), (board.Row("pr-size")!.Value, board.Row("pr-size")!.Pass));
        Assert.Equal(FakeWorld.DemoRun, board.Run);
        Assert.Equal("Integration tests", board.Jobs[0].Name);
    }

    [Fact]
    public async Task Deploy_events_are_per_environment_from_the_deploy_jobs()
    {
        var gh = new StubGitHub();
        gh.Routes[$"{Repo}/issues/events"] = "[]";
        gh.Routes[$"{Repo}/issues"] = "[]";
        gh.Routes[$"{Repo}/pulls"] = "[]";
        gh.Routes[$"{Repo}/actions/workflows/deploy.yml/runs"] = """{ "workflow_runs": [""" +
            ShaRun(920, "", "2026-11-09T14:40:00Z", "2026-11-09T14:50:00Z", "feat: low stock (#57)", "e1", status: "in_progress") + "] }";
        gh.Routes[$"{Repo}/actions/runs/920/jobs"] = """{ "jobs": [""" + string.Join(",",
            TimedJob(20, "Prepare", "success", "2026-11-09T14:40:00Z", "2026-11-09T14:41:00Z"),
            TimedJob(21, "Staging / Deploy", "success", "2026-11-09T14:41:00Z", "2026-11-09T14:45:00Z"),
            TimedJob(22, "Production / Deploy", null, "2026-11-09T14:46:00Z", null)) + "] }";

        var events = await Service(gh).Service.PollAsync(Now.AddHours(-1), Ct);

        Assert.Equal(
            [("deploy.started", "staging"), ("deploy.succeeded", "staging"), ("deploy.started", "prod")],
            events.Where(e => e.Kind.StartsWith("deploy.", StringComparison.Ordinal)).Select(e => (e.Kind, e.Subject)));
        Assert.All(events, e => Assert.Equal("main: feat: low stock (#57)", e.Detail));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 14, 46, 0, TimeSpan.Zero), events.Single(e => e.Subject == "prod").At);
    }

    [Fact]
    public void Human_review_time_ignores_bots_and_the_author_and_starts_at_the_latest_request()
    {
        var created = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var timeline = System.Text.Json.JsonDocument.Parse("""
            [
              { "event": "review_requested", "created_at": "2026-11-08T10:01:00Z", "requested_reviewer": { "login": "jbilligmeier", "type": "User" } },
              { "event": "ready_for_review", "created_at": "2026-11-08T10:20:00Z" },
              { "event": "reviewed", "state": "commented", "submitted_at": "2026-11-08T10:21:00Z", "user": { "login": "author", "type": "User" } },
              { "event": "reviewed", "state": "approved", "submitted_at": "2026-11-08T10:22:00Z", "user": { "login": "dependabot[bot]", "type": "Bot" } },
              { "event": "reviewed", "state": "changes_requested", "submitted_at": "2026-11-08T10:26:00Z", "user": { "login": "jbilligmeier", "type": "User" } }
            ]
            """).RootElement.EnumerateArray().ToList();

        var facts = Timeline.Facts(timeline, created, created.AddHours(1), "author");

        Assert.Equal(TimeSpan.FromMinutes(6), facts.HumanReview);
        Assert.Null(facts.QueueWait);
        Assert.Null(Timeline.Facts([], created, created.AddHours(1), "author").HumanReview);
    }

    /// <summary>Answers GETs from recorded JSON keyed by path; anything else is a 404.</summary>
    private sealed class StubGitHub : HttpMessageHandler
    {
        public Dictionary<string, string> Routes { get; } = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.PathAndQuery);
            return Task.FromResult(request.Method == HttpMethod.Get && Routes.TryGetValue(uri.AbsolutePath, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{ "message": "Not Found" }""") });
        }
    }
}
