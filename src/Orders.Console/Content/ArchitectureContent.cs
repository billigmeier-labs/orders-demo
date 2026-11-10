namespace Orders.Console.Content;

public sealed record ArchComponent(string Name, string Desc, IReadOnlyList<Detail> Subs, string? Explain = null);

public sealed record ArchZone(string Name, string Where, string Flex, IReadOnlyList<ArchComponent> Items);

/// <summary>"Every component, in detail" from the design spike, with names matched to docs/contracts.md.</summary>
public static class ArchitectureContent
{
    public static readonly IReadOnlyList<ArchZone> Zones =
    [
        new("GitHub", "github.com, in our organization", "1 1 520px",
        [
            new("Repository", "The code, its history, and the rules for changing it.",
            [
                new("main", "Always deployable; protected by a ruleset"),
                new("agent/<issue>-<slug> branches", "Where agents push their work"),
                new("AGENTS.md", "Commands and rules every agent reads"),
                new("CODEOWNERS", "Paths that need the human to approve"),
                new("infra/", "Terraform for everything in AWS"),
            ], "terraform"),
            new("Issues and pull requests", "All work is tracked as issues and pull requests.",
            [
                new("Issues", "From people, the incident agent and mutation tests"),
                new("Labels", "risk:*, difficulty:*, agent, needs-human, incident, mutation, demo"),
                new("Pull requests", "One per issue; auto-merge when green"),
            ], "pull-request"),
            new("Actions workflows", "Automated jobs. Each job runs on its own temporary runner machine.",
            [
                new("triage.yml", "On every new issue: score, label, assign"),
                new("agent.yml", "Claude Code works an assigned issue, opens a PR"),
                new("ci.yml", "Lint, unit, integration, build, security, PR rules; reports ci-ok"),
                new("ai-review.yml", "AI review; reports ai-review"),
                new("preview.yml", "Deploys and removes per-PR previews"),
                new("deploy.yml", "Staging, then production (and the console), on merge to main"),
                new("deploy-service.yml", "The deploy steps for one service, called by deploy.yml"),
                new("preview-reaper.yml", "Nightly; removes previews whose PR is no longer open"),
                new("incident.yml", "Started by an AWS alarm; opens an issue"),
                new("mutation.yml", "Nightly; files issues for weak tests"),
                new("release.yml", "Versions and changelog"),
                new("dependabot-auto-merge.yml", "Auto-merge for Dependabot patch and minor updates"),
                new("ai-review-override.yml", "An admin's /override-ai-review comment lets a PR through"),
                new("retry-transient.yml", "Re-runs jobs that failed on a download timeout"),
            ], "github-actions"),
            new("Merge queue", "Re-tests each ready PR on top of the latest main, then merges it.",
            [
                new("Merge group", "The PR plus everything ahead of it"),
            ], "queue"),
            new("Claude GitHub App", "Anthropic's Claude app, installed on the organization. Agents act as claude[bot] through anthropics/claude-code-action.",
            [
                new("Can", "Push branches, open pull requests, comment"),
                new("Can't", "Merge to main or approve: main needs the merge queue and code-owner review"),
            ], "agent"),
            new("Dependabot", "GitHub's built-in bot that keeps dependencies up to date with its own PRs.",
            [
                new(".github/dependabot.yml", "NuGet packages and GitHub Actions versions"),
                new("Weekly grouped PRs", "Same checks as any PR; patch and minor updates merge on their own when green"),
            ]),
        ]),
        new("AWS", "our account, us-east-1, defined in Terraform", "1.6 1 640px",
        [
            new("ECS cluster: orders-demo", "Runs every container. A service keeps tasks running; a task is one running copy of a container.",
            [
                new("service api-prod", "2 tasks of the API, each with an OpenTelemetry collector sidecar"),
                new("service api-staging", "1 task of the API, with its collector sidecar"),
                new("service pr-<N>", "1 task per open PR preview"),
                new("service console", "This demo console"),
                new("task migrate", "One-off; runs database migrations, then exits"),
                new("task loadgen", "NBomber scenarios (normal, peak, spike, soak), started from the console; requests carry X-Journey"),
            ], "ecs"),
            new("Load balancer", "Receives web traffic and sends it to the right service by hostname.",
            [
                new("demo.joshb.app", "service api-prod"),
                new("staging.demo.joshb.app", "service api-staging"),
                new("pr-<N>.demo.joshb.app", "service pr-<N>"),
                new("console.demo.joshb.app", "service console, behind Google sign-in"),
            ], "load-balancer"),
            new("ECR", "The image registry. CI builds an image once and every environment runs that same image.",
            [
                new("repository orders-api", "Release images by commit SHA; tags are immutable"),
                new("repository orders-api-preview", "PR images, pr-<N>; mutable tags, expired after 7 days"),
                new("repository orders-console", "The console image; immutable tags"),
                new("repository orders-loadgen", "The load generator image; immutable tags except latest"),
            ], "ecr"),
            new("RDS PostgreSQL", "Managed databases; AWS handles patching and backups.",
            [
                new("orders-prod › db orders", "Production data"),
                new("orders-nonprod › db orders", "Staging data"),
                new("orders-nonprod › db pr_<N>", "One per open PR preview"),
            ], "rds"),
            new("AppConfig", "Feature flags the app reads at run time, toggled from the console.",
            [
                new("Feature flags", "One set each for prod, staging and previews; the API polls every 15 s and reads them through Microsoft.FeatureManagement"),
                new("Fault flags", "Switch on known-bad paths for demos"),
            ], "appconfig"),
            new("CloudWatch and X-Ray", "Where logs, metrics, traces and alarms live.",
            [
                new("Log groups", "/ecs/orders-demo/<service>; the API logs one Serilog JSON line per request with its journey, flag values, version and trace id"),
                new("Metrics", "Load balancer requests, 5xx and latency per environment; the API's OpenTelemetry metrics per endpoint (namespace OrdersDemo)"),
                new("Alarms", "5xx count and p95 latency per service; also trigger rollback"),
                new("X-Ray", "Traces from request to database"),
            ], "cloudwatch"),
            new("EventBridge", "Routes events between services.",
            [
                new("Alarm rule", "Alarm fires or recovers › starts incident.yml in GitHub"),
            ], "eventbridge"),
            new("IAM roles", "Who may do what. GitHub Actions signs in with OIDC, so there are no AWS keys anywhere. The only repository secrets are the Claude API key and the automation app's ID and key; the console's tokens live in Secrets Manager.",
            [
                new("orders-demo-deploy", "deploy.yml on main: staging and prod"),
                new("orders-demo-preview", "PR preview workflows; preview resources only"),
                new("orders-demo-incident-readonly", "Reads logs and metrics, nothing else"),
            ], "iam"),
            new("Route 53 and ACM", "DNS names and the HTTPS certificate.",
            [
                new("demo.joshb.app and *.demo.joshb.app", "DNS records and one certificate for both"),
            ]),
            new("Terraform state and OIDC provider", "What Terraform and GitHub sign-in rely on, set up once in the infra repo.",
            [
                new("S3 state bucket", "Remembers what Terraform built"),
                new("GitHub OIDC provider", "Lets Actions sign in to AWS"),
            ], "terraform"),
            new("Cost Explorer", "What the demo costs, by tag, shown on the console.",
            [
                new("project tag", "Every demo resource carries project=orders-demo"),
                new("demo-run tag", "And demo-run=<N>, so the scoreboard can show what one run cost"),
            ], "cost-explorer"),
            new("Scoreboard snapshots", "A copy of the scoreboard per demo run, kept across teardowns.",
            [
                new("SSM /orders-demo/demo-run", "The current run number"),
                new("S3 metrics bucket › runs/<N>.json", "Written when the scoreboard is shown; read for Past runs"),
            ], "scoreboard"),
        ]),
        new("Outside services", "called over the internet", "0.8 1 300px",
        [
            new("Claude", "The AI model behind the triager, agents, AI review and incident agent.",
            [
                new("Anthropic API", "Called from Actions jobs with an API key secret"),
            ], "agent"),
            new("Google sign-in", "Who can open the console.",
            [
                new("billigmeier.org accounts", "Only these can sign in"),
            ]),
            new("1Password", "Where the few real secrets live.",
            [
                new("Infra vault", "API keys and tokens for setup"),
            ]),
        ]),
    ];
}
