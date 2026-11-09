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
        var board = await new ScoreboardService(Service(gh).Service, new FakeWorld(new FakeTimeProvider(Now), new ConsoleOptions()), new FakeTimeProvider(Now)).GetAsync(Ct);

        var clean = Assert.Single(board.Rows, r => r.Id == "agent-clean");
        Assert.Equal("Agent PRs merged without an AI-review block", clean.Name);
        Assert.Equal("1 of 4", clean.Value);
        var rollbacks = Assert.Single(board.Rows, r => r.Id == "rollbacks");
        Assert.Equal("Prod rollbacks, last 30 days", rollbacks.Name);
        Assert.Equal("1", rollbacks.Value);
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
