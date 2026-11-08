namespace Orders.Console.Domain;

/// <summary>
/// Something the console saw happen in GitHub or AWS. Shape from docs/contracts.md:
/// <c>{ at, kind, subject, detail, url }</c>.
/// Subjects: <c>issue#42</c>, <c>pr#57</c>, an environment (<c>prod</c>, <c>staging</c>) or an alarm name.
/// </summary>
public sealed record ConsoleEvent(DateTimeOffset At, string Kind, string Subject, string? Detail = null, string? Url = null)
{
    public string Key => $"{Kind}|{Subject}|{Detail}|{At.ToUnixTimeSeconds()}";

    public static string IssueSubject(int number) => $"issue#{number}";

    public static string PullSubject(int number) => $"pr#{number}";
}

public static class EventKinds
{
    public const string IssueOpened = "issue.opened";
    public const string IssueLabeled = "issue.labeled";
    public const string IssueAssigned = "issue.assigned";
    public const string PrOpened = "pr.opened";
    public const string CheckCompleted = "check.completed";
    public const string PrMerged = "pr.merged";
    public const string DeployStarted = "deploy.started";
    public const string DeploySucceeded = "deploy.succeeded";
    public const string DeployRolledBack = "deploy.rolledback";
    public const string AlarmAlarm = "alarm.alarm";
    public const string AlarmOk = "alarm.ok";
    public const string PreviewUp = "preview.up";
    public const string PreviewDown = "preview.down";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        IssueOpened, IssueLabeled, IssueAssigned, PrOpened, CheckCompleted, PrMerged,
        DeployStarted, DeploySucceeded, DeployRolledBack, AlarmAlarm, AlarmOk, PreviewUp, PreviewDown,
    };
}
