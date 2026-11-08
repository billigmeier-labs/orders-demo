namespace Orders.Console.Scenarios;

// Scenario definitions are data: one JSON file per scenario in Scenarios/Definitions.
// Values in action args and wait conditions can use {var} placeholders filled from the run
// (for example {issue} after "Create the demo issue" captures the issue number).

public sealed record ScenarioDefinition
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string Summary { get; init; } = "";

    /// <summary>The Tour is shown with the lifecycle stages and "Where this is running".</summary>
    public bool Tour { get; init; }

    public int Order { get; init; }

    public required IReadOnlyList<StepDefinition> Steps { get; init; }

    /// <summary>Clean-up actions run by Reset. Ones that reference a variable the run never set are skipped.</summary>
    public IReadOnlyList<ActionDefinition> Reset { get; init; } = [];
}

public sealed record StepDefinition
{
    public required string Id { get; init; }

    /// <summary>Lifecycle stage (issue, triage, agent, checks, queue, deploy, monitor) for the Tour.</summary>
    public string? Stage { get; init; }

    public required string Title { get; init; }

    public string Narration { get; init; } = "";

    /// <summary>Explainer opened by "What is ...?" on this step. Defaults to the stage.</summary>
    public string? Explain { get; init; }

    /// <summary>What the presenter clicks. When null the step starts waiting as soon as it is reached.</summary>
    public ActionDefinition? Action { get; init; }

    /// <summary>All of these must be observed for the step to finish.</summary>
    public IReadOnlyList<WaitCondition> Wait { get; init; } = [];

    /// <summary>Any one of these fails the step.</summary>
    public IReadOnlyList<WaitCondition> FailOn { get; init; } = [];

    public int TimeoutSeconds { get; init; } = 300;
}

public sealed record ActionDefinition
{
    public required string Label { get; init; }

    public required string Kind { get; init; }

    public IReadOnlyDictionary<string, string> Args { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Matches an event (<see cref="Kind"/>, optional <see cref="Subject"/> and <see cref="DetailContains"/>)
/// or a named live <see cref="Condition"/> such as <c>alarms-ok</c>.
/// </summary>
public sealed record WaitCondition
{
    public string? Kind { get; init; }

    public string? Subject { get; init; }

    public IReadOnlyList<string> DetailContains { get; init; } = [];

    /// <summary>Store the number in the matched event's subject (issue#42 gives 42) under this variable.</summary>
    public string? Capture { get; init; }

    public string? Condition { get; init; }

    public required string Description { get; init; }
}
