namespace Orders.Console.Content;

public sealed record Stage(string Id, string Name);

public sealed record ZoneNode(string Id, string Name, string Desc, string Explain);

public sealed record Detail(string K, string V);

public sealed record WhereRunning(IReadOnlyList<string> On, IReadOnlyList<string> Flow, IReadOnlyDictionary<string, IReadOnlyList<Detail>> Detail);

/// <summary>Lifecycle stages and "Where this is running", copied from docs/design/console-spike.dc.html.</summary>
public static class TourContent
{
    public static readonly IReadOnlyList<Stage> Stages =
    [
        new("issue", "Issue"),
        new("triage", "Triage"),
        new("agent", "Agent"),
        new("checks", "PR checks"),
        new("queue", "Merge queue"),
        new("deploy", "Deploy"),
        new("monitor", "Monitor"),
    ];

    public static readonly IReadOnlyList<ZoneNode> GitHubNodes =
    [
        new("repo", "Repository and issues", "The code, its history, issues and pull requests.", "pull-request"),
        new("runner", "Actions runners", "A temporary machine GitHub starts for each job, then throws away. Triage, agents and CI all run here.", "runner"),
        new("queue", "Merge queue", "Re-tests approved PRs on the latest main, then merges them.", "queue"),
    ];

    public static readonly IReadOnlyList<ZoneNode> AwsNodes =
    [
        new("ecr", "ECR", "Image registry: stores the app image CI built.", "ecr"),
        new("ecs", "ECS Fargate", "Runs the app containers with no servers to manage. A cluster holds services; a task is one running copy of the container.", "ecs"),
        new("rds", "RDS PostgreSQL", "The databases: one for production, one for the rest.", "rds"),
        new("cw", "CloudWatch", "Logs, metrics and alarms for every environment.", "cloudwatch"),
    ];

    public static readonly IReadOnlyList<ZoneNode> AiNodes =
    [
        new("claude", "Claude", "The AI model the triager, agents, AI review and incident agent call.", "agent"),
    ];

    private static Dictionary<string, IReadOnlyList<Detail>> D(params (string Node, Detail[] Items)[] items) =>
        items.ToDictionary(i => i.Node, i => (IReadOnlyList<Detail>)i.Items);

    public static readonly IReadOnlyDictionary<string, WhereRunning> Where = new Dictionary<string, WhereRunning>
    {
        ["issue"] = new(["repo"], ["You, in a browser", "Issue saved in the repository"], D(
            ("repo", [new("Issue #{issue}", "The demo issue, labeled demo")]))),
        ["triage"] = new(["repo", "runner", "claude"], ["New issue", "Actions starts a triage job on a runner", "Claude scores it", "Labels written back to the issue"], D(
            ("repo", [new("Issue #{issue}", "Labeled risk:*, difficulty:*, and agent when it is safe for one")]),
            ("runner", [new("Job: triage", "A fresh Linux machine, deleted when the job ends")]),
            ("claude", [new("1 request", "Reads the issue, returns risk, difficulty and reasons")]))),
        ["agent"] = new(["repo", "runner", "claude"], ["Issue assigned", "Actions starts the agent on a runner", "Claude writes code and tests", "Branch pushed to the repository"], D(
            ("repo", [new("Branch agent/{issue}-<slug>", "The agent pushes its commits here, never to main")]),
            ("runner", [new("Job: agent", "Clones the repo, installs .NET, runs just ci")]),
            ("claude", [new("Many requests", "Plans, edits files, reads test output, fixes")]))),
        ["checks"] = new(["repo", "runner", "ecr", "ecs", "rds"], ["PR opened", "Runners build and test in parallel", "Image pushed to ECR", "Preview runs on ECS with its own database"], D(
            ("repo", [new("PR #{pr}", "Closes #{issue}; auto-merge on")]),
            ("runner", [new("6 jobs at once", "One runner each: lint, build and unit tests, integration, security, PR rules, AI review"), new("Job: integration", "Starts its own throwaway Postgres container")]),
            ("ecr", [new("orders-api:pr-{pr}", "A preview-only image; ECR expires it after 7 days")]),
            ("ecs", [new("cluster orders-demo › service pr-{pr}", "1 task: one copy of the container, 0.25 vCPU"), new("pr-{pr}.demo.joshb.app", "Its own address, removed when the PR closes")]),
            ("rds", [new("orders-nonprod › db pr_{pr}", "Created for this PR, migrated, seeded")]))),
        ["queue"] = new(["repo", "runner", "queue"], ["PR joins the queue", "Re-tested on a runner against latest main", "Squash-merged into main"], D(
            ("queue", [new("#{pr}", "Tested on top of everything ahead of it")]),
            ("runner", [new("Job: CI on the merge group", "The same checks on the exact commit main will get"), new("Release image", "Built here and tagged with that commit; never rebuilt")]),
            ("repo", [new("main", "One squash commit for PR #{pr}")]))),
        ["deploy"] = new(["runner", "ecr", "ecs", "rds"], ["Runner signs in to AWS, no stored keys", "ECS pulls the image from ECR", "Migrations run on RDS", "Staging, then production"], D(
            ("runner", [new("Job: deploy", "Gets short-lived AWS credentials through OIDC")]),
            ("ecr", [new("orders-api:<commit>", "The image the merge queue built and tested")]),
            ("ecs", [new("task: migrate", "Runs once against the database, then exits"), new("service api-staging", "1 task, replaced with the new version"), new("service api-prod", "2 tasks, replaced one at a time")]),
            ("rds", [new("orders-prod › db orders", "Migration applied before the new code starts")]))),
        ["monitor"] = new(["ecs", "cw", "runner", "claude", "repo"], ["App on ECS", "CloudWatch logs and alarms", "Alarm starts the incident agent on a runner", "Claude investigates", "Issue opened"], D(
            ("ecs", [new("service api-prod", "2 tasks sending logs and metrics")]),
            ("cw", [new("log group /ecs/orders-demo/api-prod", "Every request, as structured JSON"), new("alarms", "5xx count and p95 latency, per service")]),
            ("runner", [new("Job: incident", "Starts only when an alarm fires")]),
            ("claude", [new("On an alarm", "Reads logs, recent deploys and commits")]),
            ("repo", [new("New issue", "Opened with the evidence; triage takes it from there")]))),
    };

    /// <summary>Fills {issue}/{pr} from the run, or shows N when the run has not got that far.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string>? vars) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\{([a-z]+)\}", m => vars is not null && vars.TryGetValue(m.Groups[1].Value, out var v) ? v : "N");
}
