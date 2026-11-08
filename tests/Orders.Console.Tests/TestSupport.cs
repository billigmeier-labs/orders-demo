using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orders.Console.Domain;
using Orders.Console.Events;
using Orders.Console.Scenarios;

namespace Orders.Console.Tests;

/// <summary>Records actions and returns canned outcomes.</summary>
public sealed class RecordingActions : IScenarioActions
{
    public List<(string Kind, IReadOnlyDictionary<string, string> Args)> Calls { get; } = [];

    public Dictionary<string, Func<IReadOnlyDictionary<string, string>, ActionOutcome>> Handlers { get; } = new();

    public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { "test.issue", "test.noop", "test.fail", "test.cleanup" };

    public Task<ActionOutcome> ExecuteAsync(string kind, IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        Calls.Add((kind, args));
        if (kind == "test.fail")
        {
            throw new InvalidOperationException("boom");
        }

        return Task.FromResult(Handlers.TryGetValue(kind, out var h) ? h(args) : ActionOutcome.Of(new ActionLine(kind)));
    }
}

public sealed class SwitchConditions : IConditionEvaluator
{
    public bool AlarmsOk { get; set; }

    public IReadOnlySet<string> Names { get; } = new HashSet<string> { "alarms-ok" };

    public Task<bool> EvaluateAsync(string name, CancellationToken ct) => Task.FromResult(AlarmsOk);
}

/// <summary>An engine wired to in-memory parts and a controllable clock.</summary>
public sealed class EngineHarness
{
    public EngineHarness(params ScenarioDefinition[] scenarios)
    {
        Catalog = new ScenarioCatalog(scenarios, Actions.Kinds, Conditions.Names);
        Actions.Handlers["test.issue"] = _ => new ActionOutcome(new Dictionary<string, string> { ["issue"] = "42" }, [new ActionLine("Opened issue #42")]);
        Engine = new ScenarioEngine(Catalog, Events, Actions, Conditions, new ConsoleOptions(), Time, NullLogger<ScenarioEngine>.Instance);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero));

    public EventStore Events { get; } = new();

    public RecordingActions Actions { get; } = new();

    public SwitchConditions Conditions { get; } = new();

    public ScenarioCatalog Catalog { get; }

    public ScenarioEngine Engine { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Emit(string kind, string subject, string? detail = null) =>
        Events.Add(new ConsoleEvent(Time.GetUtcNow(), kind, subject, detail));

    public Task TickAsync() => Engine.TickAsync(Ct);

    public string State(string step) => Engine.Current!.Step(step)!.State;
}

public static class Defs
{
    public static ActionDefinition Action(string kind, Dictionary<string, string>? args = null) =>
        new() { Label = kind, Kind = kind, Args = args ?? [] };

    public static WaitCondition On(string kind, string? subject = null, string[]? detail = null, string? capture = null) =>
        new() { Kind = kind, Subject = subject, DetailContains = detail ?? [], Capture = capture, Description = $"{kind} {subject}" };

    /// <summary>issue (action, captures issue) → triage (labeled agent) → pr (opened, captures pr) → merge.</summary>
    public static ScenarioDefinition Flow(string id = "flow", bool tour = true) => new()
    {
        Id = id,
        Title = id,
        Tour = tour,
        Steps =
        [
            new StepDefinition { Id = "issue", Title = "Issue", Action = Action("test.issue"), Wait = [On(EventKinds.IssueOpened, "issue#{issue}")], TimeoutSeconds = 60 },
            new StepDefinition
            {
                Id = "triage", Title = "Triage", TimeoutSeconds = 120,
                Wait = [On(EventKinds.IssueLabeled, "issue#{issue}", ["agent"])],
                FailOn = [On(EventKinds.IssueLabeled, "issue#{issue}", ["needs-human"])],
            },
            new StepDefinition { Id = "pr", Title = "PR", Wait = [On(EventKinds.PrOpened, null, ["claude[bot]", "#{issue}"], "pr")], TimeoutSeconds = 120 },
            new StepDefinition
            {
                Id = "checks", Title = "Checks", TimeoutSeconds = 120,
                Wait = [On(EventKinds.CheckCompleted, "pr#{pr}", ["ci-ok: success"]), On(EventKinds.CheckCompleted, "pr#{pr}", ["ai-review: success"])],
            },
        ],
        Reset = [Action("test.cleanup", new() { ["issue"] = "{issue}" }), Action("test.cleanup", new() { ["pr"] = "{pr}" })],
    };
}
