using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orders.Console.Domain;
using Orders.Console.Events;
using Orders.Console.Integrations;
using Orders.Console.Integrations.Fakes;
using Orders.Console.Scenarios;
using Orders.Console.Status;

namespace Orders.Console.Tests;

public sealed class FakeWorldTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero));
    private readonly FakeWorld _world;

    public FakeWorldTests() => _world = new FakeWorld(_time, new ConsoleOptions { FakeStep = TimeSpan.FromSeconds(3) });

    [Fact]
    public async Task Starts_with_realistic_status_data()
    {
        var activity = await _world.GetActivityAsync(Ct);
        Assert.True(activity.OpenIssues > 0);
        Assert.Contains(activity.OpenIssuesByLabel, l => l.Label == "mutation");
        Assert.NotEmpty(activity.AgentPulls);
        Assert.Equal(["ci", "ai-review", "deploy"], activity.LatestRuns.Select(r => r.Workflow));
        Assert.All(await _world.GetAlarmsAsync(Ct), a => Assert.Equal("OK", a.State));
        Assert.True((await _world.CheckAsync("prod", Ct)).Ready);
        Assert.Contains(await _world.GetFlagsAsync(Ct), f => f.Name == FakeWorld.FaultFlag && !f.Enabled);
        Assert.True((await _world.GetCostAsync(Ct)).MonthToDate > 0);
        Assert.NotEmpty(await _world.GetRecentDeploysAsync(Ct));
    }

    [Fact]
    public async Task A_safe_issue_goes_all_the_way_to_production()
    {
        var issue = await _world.CreateIssueAsync(new NewIssue("Add a low-stock endpoint", "Products below a threshold.", ["demo"]), Ct);
        await AdvanceAsync(10);

        var kinds = NewEvents().Select(e => (e.Kind, e.Subject, e.Detail)).ToList();
        Assert.Contains((EventKinds.IssueOpened, $"issue#{issue.Number}", $"{FakeWorld.Automation}: Add a low-stock endpoint"), kinds);
        Assert.Contains((EventKinds.IssueLabeled, $"issue#{issue.Number}", "agent"), kinds);
        var pr = NewEvents().Single(e => e.Kind == EventKinds.PrOpened);
        Assert.Contains("claude[bot]", pr.Detail, StringComparison.Ordinal);
        Assert.Contains($"#{issue.Number}", pr.Detail, StringComparison.Ordinal);
        Assert.Contains((EventKinds.CheckCompleted, pr.Subject, "ci-ok: success"), kinds);
        Assert.Contains((EventKinds.CheckCompleted, pr.Subject, "ai-review: success"), kinds);
        Assert.Contains(kinds, k => k.Kind == EventKinds.PrMerged && k.Subject == pr.Subject);
        Assert.Contains(kinds, k => k.Kind == EventKinds.DeploySucceeded && k.Subject == "prod");
        // Staging first, then production, each keyed to the PR.
        var deploys = NewEvents().Where(e => e.Kind is EventKinds.DeployStarted or EventKinds.DeploySucceeded).Select(e => (e.Kind, e.Subject)).ToList();
        Assert.Equal(
            [(EventKinds.DeployStarted, "staging"), (EventKinds.DeploySucceeded, "staging"), (EventKinds.DeployStarted, "prod"), (EventKinds.DeploySucceeded, "prod")],
            deploys);
        Assert.All(NewEvents().Where(e => e.Kind is EventKinds.DeployStarted or EventKinds.DeploySucceeded),
            e => Assert.Contains($"(#{Orders.Console.Scenarios.Matching.NumberIn(pr.Subject)})", e.Detail, StringComparison.Ordinal));

        var order = NewEvents().Select(e => e.Kind).Distinct().ToList();
        Assert.True(order.IndexOf(EventKinds.IssueLabeled) < order.IndexOf(EventKinds.PrOpened));
        Assert.True(order.IndexOf(EventKinds.PrMerged) < order.IndexOf(EventKinds.DeploySucceeded));
    }

    [Fact]
    public async Task Nothing_happens_until_time_passes()
    {
        await _world.CreateIssueAsync(new NewIssue("Something", "", []), Ct);
        await AdvanceAsync(0);
        Assert.DoesNotContain(NewEvents(), e => e.Kind == EventKinds.IssueLabeled);
    }

    [Theory]
    [InlineData("Speed up CI", "Edit `.github/workflows/ci.yml`")]
    [InlineData("Add an index", "Add an EF Core migration")]
    public async Task Risky_issues_are_routed_to_a_human(string title, string body)
    {
        var issue = await _world.CreateIssueAsync(new NewIssue(title, body, ["demo"]), Ct);
        await AdvanceAsync(2);
        var labels = NewEvents().Where(e => e.Kind == EventKinds.IssueLabeled && e.Subject == $"issue#{issue.Number}").Select(e => e.Detail).ToList();
        Assert.Contains("needs-human", labels);
        Assert.DoesNotContain("agent", labels);
    }

    [Fact]
    public async Task The_canned_bug_deploys_alarms_and_rolls_back()
    {
        await _world.OpenPullRequestAsync(new NewPullRequest(FakeWorld.CannedBugBranch, "main", "perf: cache prices", ""), Ct);
        await AdvanceAsync(5);
        Assert.Contains(await _world.GetAlarmsAsync(Ct), a => a.State == "ALARM");
        var errors = await _world.GetRecentErrorsAsync(Ct);
        Assert.Contains(errors, l => LogSummary.Summarize(l.Message) == "/api/search — PostgresException 42601: syntax error in tsquery: \"alan turing:*\"");
        Assert.Contains(errors, l => l.Message.Contains("   at Npgsql.", StringComparison.Ordinal));
        await AdvanceAsync(4);

        var kinds = NewEvents().Select(e => e.Kind).ToList();
        Assert.Contains(EventKinds.PrMerged, kinds);
        Assert.Contains(EventKinds.DeployStarted, kinds);
        Assert.Contains(EventKinds.AlarmAlarm, kinds);
        Assert.Contains(EventKinds.DeployRolledBack, kinds);
        // Staging passes (the bug only shows under load); production never succeeds.
        Assert.Contains(NewEvents(), e => e.Kind == EventKinds.DeploySucceeded && e.Subject == "staging");
        Assert.DoesNotContain(NewEvents(), e => e.Kind == EventKinds.DeploySucceeded && e.Subject == "prod");
        Assert.All(await _world.GetAlarmsAsync(Ct), a => Assert.Equal("OK", a.State));
    }

    [Fact]
    public async Task The_fault_flag_becomes_an_incident_that_an_agent_fixes()
    {
        await _world.SetFlagAsync(FakeWorld.FaultFlag, true, Ct);
        await AdvanceAsync(2);
        Assert.Contains(NewEvents(), e => e.Kind == EventKinds.AlarmAlarm);
        Assert.Contains(NewEvents(), e => e.Kind == EventKinds.IssueLabeled && e.Detail == "incident");
        await AdvanceAsync(12);

        Assert.Contains(NewEvents(), e => e.Kind == EventKinds.PrOpened && e.Detail!.StartsWith("claude[bot]: fix:", StringComparison.Ordinal));
        Assert.Contains(NewEvents(), e => e.Kind == EventKinds.DeploySucceeded);
        Assert.Contains(NewEvents(), e => e.Kind == EventKinds.AlarmOk);
        Assert.All(await _world.GetAlarmsAsync(Ct), a => Assert.Equal("OK", a.State));
    }

    [Fact]
    public async Task Load_generator_runs_for_its_duration()
    {
        await _world.StartAsync("normal", 10, Ct);
        Assert.True((await _world.GetStatusAsync(Ct)).Running);
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.False((await _world.GetStatusAsync(Ct)).Running);
    }

    [Theory]
    [InlineData("tour")]
    [InlineData("bad-deploy")]
    [InlineData("incident")]
    [InlineData("guardrails")]
    [InlineData("risky-change")]
    [InlineData("mutation")]
    [InlineData("scoreboard")]
    public async Task Every_shipped_scenario_completes_against_the_fakes(string scenarioId)
    {
        var store = new EventStore();
        var options = new ConsoleOptions { Fakes = true, FakeStep = TimeSpan.FromSeconds(3) };
        var status = new StatusService(_world, _world, _world, _world, _world, _world, _world, store, new SourceHealth(), options, _time);
        var actions = new ScenarioActions(_world, _world, _world, new ScoreboardService(_world, _world, _world, options, _time), status);
        var conditions = new ConditionEvaluator(_world, _world);
        var catalog = new ScenarioCatalog(
            ScenarioCatalog.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "Scenarios", "Definitions")),
            ScenarioActions.KnownKinds,
            conditions.Names);
        var engine = new ScenarioEngine(catalog, store, actions, conditions, options, _time, NullLogger<ScenarioEngine>.Instance);
        _time.Advance(TimeSpan.FromMinutes(5));

        for (var second = 0; second < 300 && engine.Current?.State is null or RunStates.Running; second++)
        {
            var run = engine.Current;
            var active = run?.Steps.FirstOrDefault(s => s.Id == run.ActiveStepId);
            if (run is null)
            {
                Assert.True((await engine.RunActionAsync(scenarioId, catalog.Find(scenarioId)!.Steps[0].Id, Ct)).Ok);
            }
            else if (active?.State == StepStates.Ready)
            {
                Assert.True((await engine.RunActionAsync(scenarioId, active.Id, Ct)).Ok);
            }

            _time.Advance(TimeSpan.FromSeconds(1));
            store.Add(await _world.PollAsync(DateTimeOffset.MinValue, Ct));
            await engine.TickAsync(Ct);
        }

        Assert.Equal(RunStates.Completed, engine.Current!.State);
        Assert.All(engine.Current.Steps, s => Assert.Equal(StepStates.Done, s.State));
        Assert.True((await engine.ResetAsync(scenarioId, Ct)).Ok);
    }

    private DateTimeOffset _start = DateTimeOffset.MinValue;

    private List<ConsoleEvent> NewEvents() => _world.Events.Where(e => e.At >= _start).ToList();

    private async Task AdvanceAsync(int steps)
    {
        if (_start == DateTimeOffset.MinValue)
        {
            _start = _time.GetUtcNow();
        }

        for (var i = 0; i < steps * 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await _world.GetAlarmsAsync(Ct); // any call lets the simulation catch up
        }
    }
}
