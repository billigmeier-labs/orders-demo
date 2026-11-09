using Orders.Console.Domain;
using Orders.Console.Scenarios;

namespace Orders.Console.Tests;

public sealed class ScenarioEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_step_is_ready_until_its_action_runs_then_waits()
    {
        var h = new EngineHarness(Defs.Flow());

        Assert.Null(h.Engine.Current);
        var result = await h.Engine.RunActionAsync("flow", "issue", Ct);

        Assert.True(result.Ok);
        Assert.Equal(RunStates.Running, h.Engine.Current!.State);
        Assert.Equal(StepStates.Waiting, h.State("issue"));
        Assert.Equal("42", h.Engine.Current.Vars["issue"]);
        Assert.Equal(StepStates.Pending, h.State("triage"));
    }

    [Fact]
    public async Task Advances_through_steps_as_matching_events_arrive_and_captures_variables()
    {
        var h = new EngineHarness(Defs.Flow());
        await h.Engine.RunActionAsync("flow", "issue", Ct);

        h.Emit(EventKinds.IssueOpened, "issue#42");
        await h.TickAsync();
        Assert.Equal(StepStates.Done, h.State("issue"));
        Assert.Equal(StepStates.Waiting, h.State("triage"));

        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Emit(EventKinds.IssueLabeled, "issue#42", "agent");
        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Emit(EventKinds.PrOpened, "pr#57", "claude[bot]: feat: low stock (refs #42)");
        await h.TickAsync();

        // Both steps finish in one tick because their events were already in.
        Assert.Equal(StepStates.Done, h.State("triage"));
        Assert.Equal(StepStates.Done, h.State("pr"));
        Assert.Equal("57", h.Engine.Current!.Vars["pr"]);
        Assert.Equal("checks", h.Engine.Current.ActiveStepId);
    }

    [Fact]
    public async Task The_tour_keeps_waiting_when_the_ai_review_blocks_once_and_fails_only_on_needs_human()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Scenarios", "Definitions");
        var tour = ScenarioCatalog.LoadDirectory(dir).Single(d => d.Id == "tour");
        var h = new EngineHarness(tour);
        h.Actions.Handlers["github.createIssue"] = _ => new ActionOutcome(new Dictionary<string, string> { ["issue"] = "42" }, []);
        await h.Engine.RunActionAsync("tour", "issue", Ct);
        h.Emit(EventKinds.IssueOpened, "issue#42");
        h.Emit(EventKinds.IssueLabeled, "issue#42", "agent");
        h.Emit(EventKinds.PrOpened, "pr#57", "claude[bot]: feat: low stock (refs #42)");
        await h.TickAsync();
        Assert.Equal(StepStates.Waiting, h.State("checks"));

        // First review blocks; the agent fixes it and the second review passes.
        h.Emit(EventKinds.CheckCompleted, "pr#57", "ai-review: failure");
        await h.TickAsync();
        Assert.Equal(StepStates.Waiting, h.State("checks"));
        h.Emit(EventKinds.CheckCompleted, "pr#57", "ci-ok: success");
        h.Emit(EventKinds.CheckCompleted, "pr#57", "ai-review: success");
        await h.TickAsync();
        Assert.Equal(StepStates.Done, h.State("checks"));
    }

    [Fact]
    public async Task The_tour_fails_when_the_agent_pr_is_handed_to_a_person()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Scenarios", "Definitions");
        var tour = ScenarioCatalog.LoadDirectory(dir).Single(d => d.Id == "tour");
        var h = new EngineHarness(tour);
        h.Actions.Handlers["github.createIssue"] = _ => new ActionOutcome(new Dictionary<string, string> { ["issue"] = "42" }, []);
        await h.Engine.RunActionAsync("tour", "issue", Ct);
        h.Emit(EventKinds.IssueOpened, "issue#42");
        h.Emit(EventKinds.IssueLabeled, "issue#42", "agent");
        h.Emit(EventKinds.PrOpened, "pr#57", "claude[bot]: feat: low stock (refs #42)");
        h.Emit(EventKinds.CheckCompleted, "pr#57", "ai-review: failure");
        h.Emit(EventKinds.IssueLabeled, "pr#57", "needs-human");
        await h.TickAsync();
        Assert.Equal(StepStates.Failed, h.State("checks"));
    }

    [Fact]
    public async Task Ignores_events_for_a_different_issue_or_pr()
    {
        var h = new EngineHarness(Defs.Flow());
        await h.Engine.RunActionAsync("flow", "issue", Ct);
        h.Emit(EventKinds.IssueOpened, "issue#4");
        h.Emit(EventKinds.IssueOpened, "issue#420");
        await h.TickAsync();
        Assert.Equal(StepStates.Waiting, h.State("issue"));

        h.Emit(EventKinds.IssueOpened, "issue#42");
        h.Emit(EventKinds.IssueLabeled, "issue#42", "agent");
        h.Emit(EventKinds.PrOpened, "pr#60", "claude[bot]: other work (refs #420)");
        h.Emit(EventKinds.PrOpened, "pr#61", "someone: unrelated (refs #42)");
        await h.TickAsync();

        Assert.Equal(StepStates.Waiting, h.State("pr"));
    }

    [Fact]
    public async Task A_step_with_several_waits_finishes_only_when_all_are_seen()
    {
        var h = new EngineHarness(Defs.Flow());
        await ReachChecksAsync(h);

        h.Emit(EventKinds.CheckCompleted, "pr#57", "ci-ok: success");
        await h.TickAsync();
        Assert.Equal(StepStates.Waiting, h.State("checks"));
        Assert.Equal([true, false], h.Engine.Current!.Step("checks")!.Waits.Select(w => w.Satisfied));

        h.Emit(EventKinds.CheckCompleted, "pr#57", "ai-review: success");
        await h.TickAsync();
        Assert.Equal(StepStates.Done, h.State("checks"));
        Assert.Equal(RunStates.Completed, h.Engine.Current!.State);
        Assert.Null(h.Engine.Current.ActiveStepId);
    }

    [Fact]
    public async Task A_fail_condition_fails_the_step_and_the_run()
    {
        var h = new EngineHarness(Defs.Flow());
        await h.Engine.RunActionAsync("flow", "issue", Ct);
        h.Emit(EventKinds.IssueOpened, "issue#42");
        h.Emit(EventKinds.IssueLabeled, "issue#42", "needs-human");
        await h.TickAsync();

        Assert.Equal(StepStates.Failed, h.State("triage"));
        Assert.Equal(RunStates.Failed, h.Engine.Current!.State);
        Assert.Contains("needs-human", h.Engine.Current.Step("triage")!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Times_out_after_the_step_timeout_and_can_keep_waiting()
    {
        var h = new EngineHarness(Defs.Flow());
        await h.Engine.RunActionAsync("flow", "issue", Ct);

        h.Time.Advance(TimeSpan.FromSeconds(59));
        await h.TickAsync();
        Assert.Equal(StepStates.Waiting, h.State("issue"));

        h.Time.Advance(TimeSpan.FromSeconds(2));
        await h.TickAsync();
        Assert.Equal(StepStates.TimedOut, h.State("issue"));
        Assert.Equal(RunStates.Failed, h.Engine.Current!.State);
        Assert.Contains("Still waiting for", h.Engine.Current.Step("issue")!.Message, StringComparison.Ordinal);

        var extend = await h.Engine.ExtendAsync("flow", "issue", Ct);
        Assert.True(extend.Ok);
        Assert.Equal(StepStates.Waiting, h.State("issue"));
        Assert.Equal(RunStates.Running, h.Engine.Current!.State);

        h.Emit(EventKinds.IssueOpened, "issue#42");
        await h.TickAsync();
        Assert.Equal(StepStates.Done, h.State("issue"));
    }

    [Fact]
    public async Task Only_waiting_steps_time_out_not_ones_waiting_for_the_presenter()
    {
        var h = new EngineHarness(Defs.Flow());
        await h.Engine.StartAsync("flow", Ct);
        h.Time.Advance(TimeSpan.FromHours(1));
        await h.TickAsync();
        Assert.Equal(StepStates.Ready, h.State("issue"));
    }

    [Fact]
    public async Task Reset_runs_cleanup_with_run_variables_skips_unset_ones_and_releases_the_lock()
    {
        var h = new EngineHarness(Defs.Flow());
        await h.Engine.RunActionAsync("flow", "issue", Ct);

        var reset = await h.Engine.ResetAsync("flow", Ct);

        Assert.True(reset.Ok);
        Assert.Null(h.Engine.Current);
        var cleanup = Assert.Single(h.Actions.Calls, c => c.Kind == "test.cleanup");
        Assert.Equal("42", cleanup.Args["issue"]);
        Assert.Contains(reset.Lines!, l => l.Text.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_one_scenario_runs_at_a_time()
    {
        var h = new EngineHarness(Defs.Flow("a"), Defs.Flow("b", tour: false));
        await h.Engine.RunActionAsync("a", "issue", Ct);

        var blocked = await h.Engine.RunActionAsync("b", "issue", Ct);
        Assert.False(blocked.Ok);
        Assert.Equal(409, blocked.StatusCode);
        Assert.Equal(409, (await h.Engine.StartAsync("b", Ct)).StatusCode);
        Assert.Equal(409, (await h.Engine.ResetAsync("b", Ct)).StatusCode);
        Assert.Equal("a", h.Engine.Current!.ScenarioId);

        // A failed run still holds the lock until it is reset.
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.TickAsync();
        Assert.Equal(RunStates.Failed, h.Engine.Current!.State);
        Assert.Equal(409, (await h.Engine.StartAsync("b", Ct)).StatusCode);

        await h.Engine.ResetAsync("a", Ct);
        Assert.True((await h.Engine.RunActionAsync("b", "issue", Ct)).Ok);
        Assert.Equal("b", h.Engine.Current!.ScenarioId);
    }

    [Fact]
    public async Task A_completed_run_releases_the_lock()
    {
        var h = new EngineHarness(Defs.Flow("a"), Defs.Flow("b", tour: false));
        await ReachChecksAsync(h, "a");
        h.Emit(EventKinds.CheckCompleted, "pr#57", "ci-ok: success");
        h.Emit(EventKinds.CheckCompleted, "pr#57", "ai-review: success");
        await h.TickAsync();
        Assert.Equal(RunStates.Completed, h.Engine.Current!.State);

        Assert.True((await h.Engine.StartAsync("b", Ct)).Ok);
    }

    [Fact]
    public async Task Later_steps_cannot_be_actioned_out_of_order()
    {
        var def = Defs.Flow() with
        {
            Steps = [.. Defs.Flow().Steps, new StepDefinition { Id = "extra", Title = "Extra", Action = Defs.Action("test.noop") }],
        };
        var h = new EngineHarness(def);
        Assert.Equal(409, (await h.Engine.RunActionAsync("flow", "extra", Ct)).StatusCode);
        await h.Engine.RunActionAsync("flow", "issue", Ct);
        Assert.Equal(409, (await h.Engine.RunActionAsync("flow", "extra", Ct)).StatusCode);
        Assert.Equal(409, (await h.Engine.RunActionAsync("flow", "issue", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_failed_action_fails_the_step_and_can_be_retried()
    {
        var def = new ScenarioDefinition
        {
            Id = "f",
            Title = "f",
            Tour = true,
            Steps = [new StepDefinition { Id = "one", Title = "One", Action = Defs.Action("test.fail") }],
        };
        var h = new EngineHarness(def);

        var result = await h.Engine.RunActionAsync("f", "one", Ct);
        Assert.False(result.Ok);
        Assert.Equal(StepStates.Failed, h.State("one"));
        Assert.True(h.Engine.Current!.Step("one")!.ActionFailed);

        await h.Engine.RunActionAsync("f", "one", Ct);
        Assert.Equal(2, h.Actions.Calls.Count);
    }

    [Fact]
    public async Task Steps_without_action_or_wait_finish_immediately_and_actions_without_wait_complete()
    {
        var def = new ScenarioDefinition
        {
            Id = "s",
            Title = "s",
            Tour = true,
            Steps =
            [
                new StepDefinition { Id = "go", Title = "Go", Action = Defs.Action("test.noop") },
                new StepDefinition { Id = "info", Title = "Info" },
            ],
        };
        var h = new EngineHarness(def);
        await h.Engine.RunActionAsync("s", "go", Ct);
        Assert.Equal(RunStates.Completed, h.Engine.Current!.State);
        Assert.Equal(["test.noop"], h.Engine.Current.Step("go")!.Output.Select(o => o.Text));
        Assert.Equal(StepStates.Done, h.State("info"));
    }

    [Fact]
    public async Task Uncorrelated_waits_ignore_events_from_before_the_previous_step()
    {
        var def = new ScenarioDefinition
        {
            Id = "d",
            Title = "d",
            Tour = true,
            Steps =
            [
                new StepDefinition { Id = "go", Title = "Go", Action = Defs.Action("test.noop") },
                new StepDefinition { Id = "deploy", Title = "Deploy", Wait = [Defs.On(EventKinds.DeploySucceeded, "prod")], FailOn = [Defs.On(EventKinds.DeployRolledBack, "prod")] },
            ],
        };
        var h = new EngineHarness(def);
        h.Emit(EventKinds.DeployRolledBack, "prod");
        h.Emit(EventKinds.DeploySucceeded, "prod");
        h.Time.Advance(TimeSpan.FromSeconds(20));

        await h.Engine.RunActionAsync("d", "go", Ct);
        Assert.Equal(StepStates.Waiting, h.State("deploy"));

        h.Time.Advance(TimeSpan.FromSeconds(5));
        h.Emit(EventKinds.DeploySucceeded, "prod");
        await h.TickAsync();
        Assert.Equal(StepStates.Done, h.State("deploy"));
    }

    [Fact]
    public async Task Condition_waits_check_live_state()
    {
        var def = new ScenarioDefinition
        {
            Id = "c",
            Title = "c",
            Tour = true,
            Steps = [new StepDefinition { Id = "ok", Title = "OK", Action = Defs.Action("test.noop"), Wait = [new WaitCondition { Condition = "alarms-ok", Description = "alarms OK" }] }],
        };
        var h = new EngineHarness(def);
        await h.Engine.RunActionAsync("c", "ok", Ct);
        Assert.Equal(StepStates.Waiting, h.State("ok"));

        h.Conditions.AlarmsOk = true;
        await h.TickAsync();
        Assert.Equal(RunStates.Completed, h.Engine.Current!.State);
    }

    [Fact]
    public async Task Unknown_scenarios_and_steps_are_not_found()
    {
        var h = new EngineHarness(Defs.Flow());
        Assert.Equal(404, (await h.Engine.StartAsync("nope", Ct)).StatusCode);
        await h.Engine.StartAsync("flow", Ct);
        Assert.Equal(404, (await h.Engine.RunActionAsync("flow", "nope", Ct)).StatusCode);
    }

    private static async Task ReachChecksAsync(EngineHarness h, string scenario = "flow")
    {
        await h.Engine.RunActionAsync(scenario, "issue", Ct);
        h.Emit(EventKinds.IssueOpened, "issue#42");
        h.Emit(EventKinds.IssueLabeled, "issue#42", "agent");
        h.Emit(EventKinds.PrOpened, "pr#57", "claude[bot]: feat (refs #42)");
        await h.TickAsync();
        Assert.Equal("checks", h.Engine.Current!.ActiveStepId);
    }
}
