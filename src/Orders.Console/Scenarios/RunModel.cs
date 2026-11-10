namespace Orders.Console.Scenarios;

public static class StepStates
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string TimedOut = "timedout";
    public const string Locked = "locked";
}

public static class RunStates
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public sealed record ActionLine(string Text, string? Url = null);

public sealed record ActionOutcome(IReadOnlyDictionary<string, string> Vars, IReadOnlyList<ActionLine> Lines)
{
    public static ActionOutcome Of(params ActionLine[] lines) => new(new Dictionary<string, string>(), lines);
}

public sealed record RunLogEntry(DateTimeOffset At, string Text, string? Url = null);

public sealed record WaitSnapshot(string Description, bool Satisfied);

public sealed record StepSnapshot(
    string Id,
    string Title,
    string State,
    string? ActionLabel,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? WaitStartedAt,
    DateTimeOffset? Deadline,
    DateTimeOffset? CompletedAt,
    int TimeoutSeconds,
    string? Message,
    bool ActionFailed,
    IReadOnlyList<WaitSnapshot> Waits,
    IReadOnlyList<ActionLine> Output);

public sealed record RunSnapshot(
    string Id,
    string ScenarioId,
    string Title,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? ActiveStepId,
    IReadOnlyDictionary<string, string> Vars,
    IReadOnlyList<StepSnapshot> Steps,
    IReadOnlyList<RunLogEntry> Log)
{
    public StepSnapshot? Step(string id) => Steps.FirstOrDefault(s => s.Id == id);

    /// <summary>Holds the one-scenario-at-a-time lock until it completes or is reset.</summary>
    public bool HoldsLock => State != RunStates.Completed;
}

public sealed record EngineResult(bool Ok, string? Error = null, int StatusCode = 200, IReadOnlyList<ActionLine>? Lines = null)
{
    public static EngineResult Success(IReadOnlyList<ActionLine>? lines = null) => new(true, Lines: lines);

    public static EngineResult NotFound(string error) => new(false, error, 404);

    public static EngineResult Conflict(string error) => new(false, error, 409);
}

public interface IScenarioActions
{
    IReadOnlySet<string> Kinds { get; }

    Task<ActionOutcome> ExecuteAsync(string kind, IReadOnlyDictionary<string, string> args, CancellationToken ct);
}

public interface IConditionEvaluator
{
    IReadOnlySet<string> Names { get; }

    /// <param name="subject">The condition's argument with run variables filled in, e.g. "pr#57"; null when it has none.</param>
    Task<bool> EvaluateAsync(string name, string? subject, CancellationToken ct);
}

public sealed class MissingVariableException(string name) : Exception($"The run has no value for {{{name}}} yet.")
{
    public string Name { get; } = name;
}
