using Orders.Console.Content;
using Orders.Console.Scenarios;

namespace Orders.Console.Pages;

public sealed record StepView(int Index, StepDefinition Def, string State, StepSnapshot? Snap)
{
    public bool CanAct => Def.Action is not null && (State == StepStates.Ready || (State == StepStates.Failed && Snap?.ActionFailed == true));

    public bool CanExtend => State == StepStates.TimedOut;

    public string? ExplainId => Def.Explain ?? Def.Stage;
}

/// <summary>Everything the Present page needs to draw one scenario, with or without a run in progress.</summary>
public sealed record RunView(
    ScenarioDefinition Scenario,
    RunSnapshot? Run,
    string? LockedBy,
    IReadOnlyList<StepView> Steps,
    int Focus,
    IReadOnlyList<ScenarioDefinition> Library,
    RunSnapshot? AnyRun,
    DateTimeOffset Now)
{
    public StepView FocusStep => Steps[Focus];

    public string RunState => Run?.State ?? RunStates.Idle;

    public int ActiveIndex => Run is null ? 0 : Run.State == RunStates.Completed ? Steps.Count - 1 : Math.Max(0, Steps.ToList().FindIndex(s => s.Def.Id == Run.ActiveStepId));

    public IReadOnlyDictionary<string, string>? Vars => Run?.Vars;

    public static RunView Build(ScenarioCatalog catalog, ScenarioDefinition scenario, RunSnapshot? current, string? view, DateTimeOffset now)
    {
        var run = current?.ScenarioId == scenario.Id ? current : null;
        var lockedBy = current is not null && current.ScenarioId != scenario.Id && current.HoldsLock ? current.Title : null;
        var steps = scenario.Steps.Select((s, i) =>
        {
            var snap = run?.Step(s.Id);
            var state = snap?.State ?? (i == 0 ? (lockedBy is null ? StepStates.Ready : StepStates.Locked) : StepStates.Pending);
            if (snap is null && i == 0 && s.Action is null)
            {
                state = StepStates.Pending;
            }

            return new StepView(i, s, state, snap);
        }).ToList();

        var active = run is null ? 0 : run.State == RunStates.Completed ? steps.Count - 1 : Math.Max(0, steps.FindIndex(s => s.Def.Id == run.ActiveStepId));
        var focus = view is not null && steps.FindIndex(s => s.Def.Id == view) is >= 0 and var v ? v : active;
        return new RunView(scenario, run, lockedBy, steps, focus, catalog.Library.ToList(), current, now);
    }

    public string StageName(string? stage) => TourContent.Stages.FirstOrDefault(s => s.Id == stage)?.Name ?? Scenario.Title;
}
