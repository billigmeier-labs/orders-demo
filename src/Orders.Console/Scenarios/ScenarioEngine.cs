using Orders.Console.Domain;
using Orders.Console.Events;

namespace Orders.Console.Scenarios;

/// <summary>
/// Runs one scenario at a time. A step with an action is "ready" until the presenter clicks it; then
/// (or straight away when it has no action) it is "waiting" until every wait condition has been
/// observed, any fail condition is observed (failed), or its timeout passes (timedout).
/// The run holds the lock until it completes or is reset.
/// </summary>
public sealed partial class ScenarioEngine(
    ScenarioCatalog catalog,
    EventStore events,
    IScenarioActions actions,
    IConditionEvaluator conditions,
    ConsoleOptions options,
    TimeProvider time,
    ILogger<ScenarioEngine> logger) : IDisposable
{
    /// <summary>Events may carry a source timestamp slightly before the console saw the step start.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Run? _run;
    private volatile RunSnapshot? _snapshot;

    public RunSnapshot? Current => _snapshot;

    public void Dispose() => _gate.Dispose();

    public async Task<EngineResult> StartAsync(string scenarioId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var def = catalog.Find(scenarioId);
            if (def is null)
            {
                return EngineResult.NotFound($"No scenario called '{scenarioId}'.");
            }

            if (LockedBy(scenarioId) is { } other)
            {
                return EngineResult.Conflict($"'{other}' is still running. Reset it first.");
            }

            if (_run is { State: RunStates.Running or RunStates.Failed } && _run.Def.Id == scenarioId)
            {
                return EngineResult.Conflict($"'{def.Title}' is already running. Reset it to start again.");
            }

            await BeginRunAsync(def, ct);
            return EngineResult.Success();
        }
        finally
        {
            Publish();
            _gate.Release();
        }
    }

    public async Task<EngineResult> RunActionAsync(string scenarioId, string stepId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var def = catalog.Find(scenarioId);
            if (def is null)
            {
                return EngineResult.NotFound($"No scenario called '{scenarioId}'.");
            }

            if (LockedBy(scenarioId) is { } other)
            {
                return EngineResult.Conflict($"'{other}' is still running. Reset it first.");
            }

            var isFirst = def.Steps.Count > 0 && def.Steps[0].Id == stepId;
            if (_run is null || _run.Def.Id != scenarioId || _run.State == RunStates.Completed)
            {
                if (!isFirst)
                {
                    return EngineResult.Conflict("Start the scenario from its first step.");
                }

                await BeginRunAsync(def, ct);
            }

            var run = _run!;
            var step = run.Steps.FirstOrDefault(s => s.Def.Id == stepId);
            if (step is null)
            {
                return EngineResult.NotFound($"No step called '{stepId}'.");
            }

            var actionable = step.State == StepStates.Ready || (step.State == StepStates.Failed && step.ActionFailed);
            if (step.Def.Action is null || !actionable || run.Steps[run.Active] != step)
            {
                return EngineResult.Conflict($"Step '{step.Def.Title}' is {step.State}, not ready for its action.");
            }

            return await ExecuteActionAsync(run, step, ct);
        }
        finally
        {
            Publish();
            _gate.Release();
        }
    }

    /// <summary>"Keep waiting": gives a timed-out step another full timeout.</summary>
    public async Task<EngineResult> ExtendAsync(string scenarioId, string stepId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var step = _run?.Def.Id == scenarioId ? _run.Steps.FirstOrDefault(s => s.Def.Id == stepId) : null;
            if (_run is null || step is null || step.State != StepStates.TimedOut)
            {
                return EngineResult.Conflict("Only a timed-out step can keep waiting.");
            }

            var now = time.GetUtcNow();
            step.State = StepStates.Waiting;
            step.Deadline = now + TimeSpan.FromSeconds(step.Def.TimeoutSeconds);
            step.Message = null;
            _run.State = RunStates.Running;
            _run.EndedAt = null;
            AddLog(_run, $"Waiting another {step.Def.TimeoutSeconds} s for: {step.Def.Title}");
            await EvaluateAsync(_run, ct);
            return EngineResult.Success();
        }
        finally
        {
            Publish();
            _gate.Release();
        }
    }

    /// <summary>Runs the scenario's clean-up actions and releases the lock.</summary>
    public async Task<EngineResult> ResetAsync(string scenarioId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var def = catalog.Find(scenarioId);
            if (def is null)
            {
                return EngineResult.NotFound($"No scenario called '{scenarioId}'.");
            }

            if (LockedBy(scenarioId) is { } other)
            {
                return EngineResult.Conflict($"'{other}' is running; reset that one.");
            }

            var vars = _run?.Def.Id == scenarioId ? _run.Vars : new Dictionary<string, string>();
            var lines = new List<ActionLine>();
            foreach (var action in def.Reset)
            {
                try
                {
                    var args = action.Args.ToDictionary(kv => kv.Key, kv => Matching.Fill(kv.Value, vars));
                    var outcome = await actions.ExecuteAsync(action.Kind, args, ct);
                    lines.Add(new ActionLine($"{action.Label}: done"));
                    lines.AddRange(outcome.Lines);
                }
                catch (MissingVariableException)
                {
                    lines.Add(new ActionLine($"{action.Label}: skipped, nothing to undo"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogResetActionFailed(ex, action.Kind, scenarioId);
                    lines.Add(new ActionLine($"{action.Label}: failed ({ex.Message})"));
                }
            }

            if (_run?.Def.Id == scenarioId)
            {
                _run = null;
            }

            return EngineResult.Success(lines);
        }
        finally
        {
            Publish();
            _gate.Release();
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_run is { State: RunStates.Running })
            {
                await EvaluateAsync(_run, ct);
            }
        }
        finally
        {
            Publish();
            _gate.Release();
        }
    }

    private string? LockedBy(string scenarioId) =>
        _run is { } r && r.Def.Id != scenarioId && r.State != RunStates.Completed ? r.Def.Title : null;

    private async Task BeginRunAsync(ScenarioDefinition def, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        _run = new Run
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Def = def,
            StartedAt = now,
            Cursor = now,
            Steps = def.Steps.Select(s => new StepRun(s)).ToArray(),
        };
        _run.Vars["agent"] = options.AgentLogin;
        AddLog(_run, $"Started {def.Title}");
        LogStarted(def.Id, _run.Id);
        Activate(_run, 0, now);
        await EvaluateAsync(_run, ct);
    }

    private async Task<EngineResult> ExecuteActionAsync(Run run, StepRun step, CancellationToken ct)
    {
        var action = step.Def.Action!;
        step.State = StepStates.Running;
        step.ActionFailed = false;
        step.Message = null;
        run.State = RunStates.Running;
        run.EndedAt = null;
        Publish();
        try
        {
            var args = action.Args.ToDictionary(kv => kv.Key, kv => Matching.Fill(kv.Value, run.Vars));
            var outcome = await actions.ExecuteAsync(action.Kind, args, ct);
            foreach (var (k, v) in outcome.Vars)
            {
                run.Vars[k] = v;
            }

            step.Output.AddRange(outcome.Lines);
            foreach (var line in outcome.Lines)
            {
                AddLog(run, line.Text, line.Url);
            }

            BeginWait(step, time.GetUtcNow());
            await EvaluateAsync(run, ct);
            return EngineResult.Success(outcome.Lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogActionFailed(ex, action.Kind, run.Def.Id);
            step.State = StepStates.Failed;
            step.ActionFailed = true;
            step.Message = $"{action.Label} failed: {ex.Message}";
            run.State = RunStates.Failed;
            run.EndedAt = time.GetUtcNow();
            AddLog(run, step.Message);
            return new EngineResult(false, step.Message, 502);
        }
    }

    private static void Activate(Run run, int index, DateTimeOffset now)
    {
        run.Active = index;
        var step = run.Steps[index];
        step.ActivatedAt = now;
        step.Floor = run.Cursor;
        if (step.Def.Action is not null)
        {
            step.State = StepStates.Ready;
        }
        else
        {
            BeginWait(step, now);
        }
    }

    private static void BeginWait(StepRun step, DateTimeOffset now)
    {
        step.State = StepStates.Waiting;
        step.WaitStartedAt = now;
        step.Deadline = now + TimeSpan.FromSeconds(step.Def.TimeoutSeconds);
    }

    private async Task EvaluateAsync(Run run, CancellationToken ct)
    {
        // Loop so that several steps can finish in one tick when their events are already in.
        while (run.State == RunStates.Running)
        {
            var step = run.Steps[run.Active];
            if (step.State != StepStates.Waiting)
            {
                return;
            }

            var now = time.GetUtcNow();
            var seen = events.Since((step.Def.Wait.Concat(step.Def.FailOn).Any(w => w.SinceRunStart) ? run.StartedAt : step.Floor) - ClockSlack);

            // Correlated conditions (about issue #{issue}, PR #{pr}) tolerate clock skew between the
            // console and GitHub/AWS. Uncorrelated ones (any deploy, any alarm) only count events after
            // the previous step's evidence, so an earlier run's deploy or rollback cannot match.
            bool InWindow(WaitCondition c, ConsoleEvent e) =>
                e.At >= (!Matching.IsCorrelated(c) ? step.Floor : c.SinceRunStart ? run.StartedAt - ClockSlack : step.Floor - ClockSlack);

            foreach (var fail in step.Def.FailOn)
            {
                var hit = seen.FirstOrDefault(e => InWindow(fail, e) && Matching.Matches(fail, e, run.Vars));
                if (hit is not null)
                {
                    step.State = StepStates.Failed;
                    step.Message = $"{fail.Description} ({Describe(hit)})";
                    run.State = RunStates.Failed;
                    run.EndedAt = now;
                    AddLog(run, $"Failed: {step.Message}", hit.Url);
                    return;
                }
            }

            for (var i = 0; i < step.Def.Wait.Count; i++)
            {
                if (step.Satisfied[i])
                {
                    continue;
                }

                var wait = step.Def.Wait[i];
                if (wait.Condition is not null)
                {
                    string? subject = null;
                    if (wait.Subject is not null && !Matching.TryFill(wait.Subject, run.Vars, out subject))
                    {
                        continue;
                    }

                    if (await conditions.EvaluateAsync(wait.Condition, subject, ct))
                    {
                        step.Satisfied[i] = true;
                        step.LatestMatch = Max(step.LatestMatch, now);
                        AddLog(run, $"Observed: {wait.Description}");
                    }

                    continue;
                }

                var match = seen.FirstOrDefault(e => InWindow(wait, e) && Matching.Matches(wait, e, run.Vars));
                if (match is null)
                {
                    continue;
                }

                step.Satisfied[i] = true;
                step.LatestMatch = Max(step.LatestMatch, match.At);
                if (wait.Capture is not null && Matching.NumberIn(match.Subject) is { } number)
                {
                    run.Vars[wait.Capture] = number;
                }

                if (match.Url is not null && wait.Capture is not null)
                {
                    run.Vars[$"{wait.Capture}Url"] = match.Url;
                }

                AddLog(run, $"Observed: {wait.Description} ({Describe(match)})", match.Url);
            }

            if (step.Satisfied.All(s => s))
            {
                step.State = StepStates.Done;
                step.CompletedAt = now;
                run.Cursor = Max(run.Cursor, step.LatestMatch ?? now);
                if (run.Active + 1 >= run.Steps.Length)
                {
                    run.State = RunStates.Completed;
                    run.EndedAt = now;
                    AddLog(run, $"{run.Def.Title} finished");
                    return;
                }

                Activate(run, run.Active + 1, now);
                continue;
            }

            if (now >= step.Deadline)
            {
                var pending = step.Def.Wait.Where((_, i) => !step.Satisfied[i]).Select(w => w.Description);
                step.State = StepStates.TimedOut;
                step.Message = $"Nothing after {step.Def.TimeoutSeconds} s. Still waiting for: {string.Join("; ", pending)}";
                run.State = RunStates.Failed;
                run.EndedAt = now;
                AddLog(run, $"Timed out: {step.Def.Title}");
            }

            return;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset b) => a is null || b > a ? b : a;

    private static string Describe(ConsoleEvent e) =>
        e.Detail is null ? $"{e.Kind} {e.Subject}" : $"{e.Kind} {e.Subject}: {e.Detail}";

    private void AddLog(Run run, string text, string? url = null)
    {
        run.Log.Add(new RunLogEntry(time.GetUtcNow(), text, url));
        if (run.Log.Count > 200)
        {
            run.Log.RemoveAt(0);
        }
    }

    private void Publish()
    {
        var run = _run;
        _snapshot = run is null ? null : new RunSnapshot(
            run.Id,
            run.Def.Id,
            run.Def.Title,
            run.State,
            run.StartedAt,
            run.EndedAt,
            run.State == RunStates.Completed ? null : run.Steps[run.Active].Def.Id,
            new Dictionary<string, string>(run.Vars),
            run.Steps.Select(s => new StepSnapshot(
                s.Def.Id,
                s.Def.Title,
                s.State,
                s.Def.Action?.Label,
                s.ActivatedAt,
                s.WaitStartedAt,
                s.Deadline == DateTimeOffset.MinValue ? null : s.Deadline,
                s.CompletedAt,
                s.Def.TimeoutSeconds,
                s.Message,
                s.ActionFailed,
                s.Def.Wait.Select((w, i) => new WaitSnapshot(w.Description, s.Satisfied[i])).ToList(),
                s.Output.ToList())).ToList(),
            run.Log.ToList());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scenario {Scenario} started as run {Run}")]
    private partial void LogStarted(string scenario, string run);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Action {Kind} failed in scenario {Scenario}")]
    private partial void LogActionFailed(Exception ex, string kind, string scenario);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reset action {Kind} failed in scenario {Scenario}")]
    private partial void LogResetActionFailed(Exception ex, string kind, string scenario);

    private sealed class Run
    {
        public required string Id { get; init; }

        public required ScenarioDefinition Def { get; init; }

        public required DateTimeOffset StartedAt { get; init; }

        public required StepRun[] Steps { get; init; }

        public string State { get; set; } = RunStates.Running;

        public DateTimeOffset? EndedAt { get; set; }

        public int Active { get; set; }

        public DateTimeOffset Cursor { get; set; }

        public Dictionary<string, string> Vars { get; } = new(StringComparer.Ordinal);

        public List<RunLogEntry> Log { get; } = [];
    }

    private sealed class StepRun(StepDefinition def)
    {
        public StepDefinition Def { get; } = def;

        public string State { get; set; } = StepStates.Pending;

        public bool ActionFailed { get; set; }

        public DateTimeOffset? ActivatedAt { get; set; }

        public DateTimeOffset? WaitStartedAt { get; set; }

        public DateTimeOffset Deadline { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset? CompletedAt { get; set; }

        public DateTimeOffset Floor { get; set; }

        public DateTimeOffset? LatestMatch { get; set; }

        public string? Message { get; set; }

        public bool[] Satisfied { get; } = new bool[def.Wait.Count];

        public List<ActionLine> Output { get; } = [];
    }
}
