# Orders.Console: the demo console

The presenter's web app for showing the agent-first delivery loop to people who know Azure but not
GitHub, Actions or AWS. ASP.NET Core (.NET 10) Razor Pages, server-rendered, with one small script
(`wwwroot/js/console.js`) that polls live regions every few seconds, posts actions and opens explainers.
Design and content come from `docs/design/console-spike.dc.html`.

| Mode | Path | What it shows |
| --- | --- | --- |
| Status | `/` (also `/status`) | Prod and staging health (`/health/ready`, `/api/version`) with requests, 5xx rate and p95 latency from the load balancer, CloudWatch alarms, latest prod errors, GitHub activity (open issues by triage label, agent PRs in flight, latest ci / ai-review / deploy runs), deploys and rollbacks, cost (month to date, today), feature flags with toggles, load generator controls (normal, peak, spike, soak, for N minutes), and every event the console has seen |
| Present | `/present`, `/present?scenario=<id>` | The Tour (10 steps) and the scenario library, driven by real events |
| Architecture | `/architecture` | The spike's diagram and component cards (static) |

Every lifecycle stage and infrastructure term opens an explainer drawer: what it is, what it is built
with, the Azure equivalent, and a link to the real file or console.

## Run it locally (fake mode)

No `dotnet` on the host? Build and run in containers:

```sh
docker buildx build --platform linux/amd64 -f src/Orders.Console/Dockerfile -t orders-console:dev --load .
docker run --rm -p 8080:8080 -e CONSOLE_FAKES=true -e CONSOLE_AUTH=off orders-console:dev
# http://localhost:8080
```

`CONSOLE_FAKES=true` swaps every GitHub and AWS dependency for `Integrations/Fakes/FakeWorld.cs`: realistic
history plus a simulation that reacts to the console's actions one step (`CONSOLE_FAKE_STEP_SECONDS`, default 3)
at a time. A new issue is triaged (`.github` or `migration` in the text goes to `needs-human`), an agent
opens a PR, checks pass, the queue merges, staging then production succeed; the `demo/canned-bug` PR deploys, alarms and
rolls back; the `demo/guardrails` PR fails PR rules and needs a code owner; turning on `searchV2` fires an alarm, opens an
`incident` issue, and an agent's fix clears it. Load raises production's traffic, and the scoreboard keeps snapshots in memory.

The Dockerfile is multi-stage, restores in locked mode from `packages.lock.json`, runs as the image's non-root
`app` user on port 8080, and cross-publishes, so `--platform linux/arm64` builds on an amd64 host without emulation.
Build context: the repo root (like the API).

## Configuration

| Env var | Default | Meaning |
| --- | --- | --- |
| `CONSOLE_FAKES` | `false` | `true` uses the simulated GitHub and AWS |
| `CONSOLE_AUTH` | on | `off` disables sign-in; refused unless `CONSOLE_FAKES=true` |
| `CONSOLE_VERIFICATION_TOKEN` | | Requests with header `X-Verification-Token` equal to this are signed in as `automation` (constant-time compare) |
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | | Google OpenID Connect, callback `/signin-oidc` |
| `GOOGLE_HOSTED_DOMAIN` | `billigmeier.org` | Only verified accounts of this Google Workspace domain get in |
| `DATAPROTECTION_KEYS_PATH` | | Directory for Data Protection keys (sign-in cookies survive restarts and work across tasks) |
| `GITHUB_TOKEN` | | Token for the GitHub API (the automation PAT): issues, PRs, checks, Actions read; issues and PRs write |
| `GITHUB_REPOSITORY` | `billigmeier-labs/orders-demo` | |
| `GITHUB_API_URL` | `https://api.github.com` | |
| `AGENT_LOGIN` | `claude[bot]` | Author of agent PRs |
| `GITHUB_WORKFLOWS` | `ci.yml,ai-review.yml,deploy.yml` | Workflows shown on Status; the first is used for CI duration (per run and per job) |
| `GITHUB_DEPLOY_WORKFLOW` | `deploy.yml` | Its Staging and Production jobs become `deploy.started` / `deploy.succeeded` for `staging` / `prod` |
| `PROD_URL`, `STAGING_URL` | `https://demo.joshb.app`, `https://staging.demo.joshb.app` | Health probes |
| `AWS_REGION` | `us-east-1` | Cost Explorer always uses us-east-1. Credentials come from the default chain (ECS task role) |
| `ECS_CLUSTER`, `ECS_PROD_SERVICE`, `ECS_STAGING_SERVICE` | `demo`, `prod`, `staging` | Deployments and rollbacks |
| `ALARM_PREFIX` | `orders-` | Alarms shown and watched; the deploy alarms' `LoadBalancer`/`TargetGroup` dimensions (alarm names containing the ECS service names) say where to read each environment's ALB traffic |
| `PROD_LOG_GROUP` | `/ecs/orders-api/prod` | Latest errors |
| `ERROR_LOG_FILTER` | `?ERROR ?Error ?Exception` | CloudWatch Logs filter pattern |
| `COST_PROJECT_TAG` | `orders-demo` | Cost Explorer filter on tag `project` (and `demo-run=<N>` for the run's cost) |
| `DEMO_RUN_PARAMETER` | `/orders-demo/demo-run` | SSM parameter with the current demo run number |
| `METRICS_BUCKET_PARAMETER`, `METRICS_BUCKET` | `/orders-demo/metrics-bucket`, unset | Where scoreboard snapshots go (`runs/<N>.json`); `METRICS_BUCKET` overrides the SSM lookup |
| `MUTATION_SCORE_TARGET` | 60 | The scoreboard's mutation score target (Stryker's `low` threshold) |
| `APPCONFIG_APPLICATION`, `APPCONFIG_ENVIRONMENT`, `APPCONFIG_PROFILE` | `orders-demo`, `prod`, `feature-flags` | Name or id of the feature-flag profile |
| `APPCONFIG_DEPLOYMENT_STRATEGY` | `orders-demo-instant` | Name or id of the deployment strategy for flag toggles (see below) |
| `LOADGEN_TASK_DEFINITION`, `LOADGEN_CONTAINER` | `loadgen`, `loadgen` | Started with env `SCENARIO` and `DURATION_MINUTES`, `startedBy=orders-console` |
| `LOADGEN_SUBNETS`, `LOADGEN_SECURITY_GROUPS` | | Comma-separated; required to start load |
| `LOADGEN_ASSIGN_PUBLIC_IP` | `false` | |
| `GITHUB_POLL_SECONDS`, `AWS_POLL_SECONDS`, `ENGINE_TICK_SECONDS` | 10, 15, 2 (fakes: 1, 1, 1) | Event polling |
| `STATUS_REFRESH_SECONDS` | 5 | Browser refresh of the Status page |
| `CONSOLE_FAKE_STEP_SECONDS` | 3 | Pace of the fake simulation |

Behind the ALB, `X-Forwarded-For` and `X-Forwarded-Proto` are trusted. `/health/live` is anonymous; everything
else needs Google sign-in or the verification token. Page fetches and `/api/*` get a 401 instead of a redirect.

### AppConfig deployment strategy

Flag toggles deploy a new configuration version, and AppConfig allows one deployment per environment at a time,
including its bake time. The predefined `AppConfig.AllAtOnce` bakes for 10 minutes, so a second toggle (or the
incident scenario's reset) would fail for 10 minutes. The console expects a custom strategy named
**`orders-demo-instant`**: `deployment_duration_in_minutes = 0`, `final_bake_time_in_minutes = 0`,
`growth_factor = 100`, `growth_type = LINEAR`, `replicate_to = NONE`. Another name works if you set
`APPCONFIG_DEPLOYMENT_STRATEGY`; names starting with `AppConfig.` are used as ids directly.

### In ECS

Secrets (from Secrets Manager or SSM): `GITHUB_TOKEN` (the automation PAT), `CONSOLE_VERIFICATION_TOKEN`,
`GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`. Plain env: `LOADGEN_SUBNETS`, `LOADGEN_SECURITY_GROUPS`, and any
names that differ from the defaults above (cluster, services, alarm prefix, log group, AppConfig names,
loadgen task definition). Set `DATAPROTECTION_KEYS_PATH` to a mounted EFS directory if the service runs more
than one task or should keep sign-ins across deploys. Health check path: `/health/live`. Google OAuth redirect URI:
`https://console.demo.joshb.app/signin-oidc`.

IAM the task role needs: `cloudwatch:DescribeAlarms`, `cloudwatch:DescribeAlarmHistory`, `cloudwatch:GetMetricData`, `logs:FilterLogEvents`,
`ecs:DescribeServices`, `ecs:DescribeTaskDefinition`, `ecs:ListServiceDeployments`, `ecs:DescribeServiceRevisions`, `ecs:RunTask`, `ecs:TagResource` (on RunTask), `ecs:StopTask` (only tasks tagged `component=loadgen`), `ecs:ListTasks`, `ecs:DescribeTasks`, `iam:PassRole` (loadgen roles),
`appconfig:List*`, `appconfig:GetHostedConfigurationVersion`, `appconfig:CreateHostedConfigurationVersion`,
`appconfig:StartDeployment`, `ce:GetCostAndUsage`, `ssm:GetParameter` on `/orders-demo/demo-run` and `/orders-demo/metrics-bucket`,
`s3:ListBucket` (prefix `runs/`) on the metrics bucket and `s3:GetObject` / `s3:PutObject` on `runs/*`. Without the ECS
deployment-history permissions the deploy list falls back to the services' current deployments. For the run's cost, `demo-run`
must be an activated cost allocation tag.

## How it works

- `Integrations/Contracts.cs`: one interface per dependency (`IGitHubService`, `IMonitoringService`, `IDeploymentService`,
  `ICostService`, `IFeatureFlagService`, `ILoadGenerator`, `IEnvironmentProbe`, `IEventSource`). `Program.cs` registers the
  real ones (`Integrations/GitHub`, `Integrations/Aws`, `Integrations/Http`) or `FakeWorld` for all of them.
- `Events/EventCollector.cs` polls every `IEventSource` and records events `{ at, kind, subject, detail, url }`
  (kinds from `docs/contracts.md`). Subjects: `issue#42`, `pr#57`, `prod`/`staging`, or an alarm name.
  GitHub: issue events, issues, PRs, check runs of open PRs (`ci-ok`, `ai-review`, `PR rules`, `Preview up`), the deploy
  workflow's Staging and Production jobs, all with ETags. AWS: CloudWatch alarm history, ECS deployments that failed or rolled back.
- `Status/ScoreboardService.cs` measures the spec's targets from GitHub (PRs, PR timelines for merge queue and review times,
  Actions runs and jobs, the nightly mutation run's `mutation-score` annotation, `flaky` issues) and Cost Explorer, and saves
  each run's snapshot to S3. Completed runs and merged PRs are read once and kept; the whole board is cached 5 minutes.
- `Scenarios/ScenarioEngine.cs` runs one scenario at a time (in-memory lock, held until the run completes or is reset).
  A step with an action is `ready` until clicked; then it is `waiting` until every `wait` condition is seen, any
  `failOn` is seen (`failed`), or `timeoutSeconds` passes (`timedout`; "Keep waiting" extends it). Reset runs the
  scenario's clean-up actions.
- `Status/StatusService.cs` caches each panel briefly in real mode (cost for an hour: Cost Explorer charges per call).

### Scenario files

One JSON file per scenario in `Scenarios/Definitions` (tour, bad-deploy, incident, guardrails, risky-change, mutation,
scoreboard). Add a file to add a scenario; the app refuses to start if one is invalid.

```jsonc
{
  "id": "guardrails", "title": "...", "summary": "...", "order": 30,
  "steps": [
    {
      "id": "issue", "stage": "issue", "title": "...", "narration": "...", "explain": "issue",
      "action": { "label": "Create the guardrails issue", "kind": "github.createIssue", "args": { "title": "...", "body": "...", "labels": "demo" } },
      "wait":   [ { "kind": "issue.opened", "subject": "issue#{issue}", "description": "GitHub records the new issue" } ],
      "failOn": [ ],
      "timeoutSeconds": 120
    }
  ],
  "reset": [ { "label": "Close the issue", "kind": "github.closeIssue", "args": { "number": "{issue}" } } ]
}
```

- Action kinds: `github.createIssue` (sets `{issue}`), `github.closeIssue`, `github.openPullRequest` (sets `{pr}`, turns on
  auto-merge), `github.closePullRequest`, `github.listIssues`, `github.listAwaitingReview`, `load.start`, `load.stop`,
  `flags.set`, `scoreboard.show` (sets `{run}`, `{snapshot}`, `{snapshotUrl}`), `scoreboard.pastRuns`, `note.show`
  (`text` lines, the last linked to `url`).
- A step may have a `command` shown in a terminal box (the scoreboard's `just env down`).
- A wait is an event (`kind`, optional `subject`, `detailContains`, and `capture` to store the number from the subject)
  or a live `condition`: `alarms-ok`, `prod-traffic` (production's latest minute has at least 100 requests),
  `review-required` with `subject` `pr#{pr}` (GitHub's reviewDecision says it needs a review). `{agent}` is `AGENT_LOGIN`.
  `detailContains` is case-insensitive and `#4` does not match `#42`.
- Waits that name `{issue}`/`{pr}` accept events up to 60 s before the step started (clock skew), or since the run started
  with `"sinceRunStart": true` (a preview can be up before the checks finish); other waits only count events after the
  previous step's evidence, so an old deploy or alarm cannot satisfy a new run. Deploy waits name `#{pr}`.

## Hooks for the Playwright suite

| Element | Selector |
| --- | --- |
| Mode tabs | `[data-testid=nav-status]`, `nav-present`, `nav-architecture` (current one has `aria-current=page`) |
| Fake mode badge | `[data-testid=fake-mode]`; also `body[data-fakes=true]` |
| Signed-in identity | `[data-testid=identity]` |
| Status panels | `[data-panel=health|alarms|deploys|cost|github|flags|load|errors|events]`; `[data-testid=panel-error]` when a source fails |
| Environment health | `[data-env=prod][data-health=ready|down]`, `[data-testid=env-version]`; traffic `[data-testid=traffic-prod|traffic-staging][data-latest-requests]` with `traffic-rate`, `traffic-errors`, `traffic-p95` |
| GitHub activity | `[data-testid=open-issues]`, `[data-testid=triage-labels] [data-triage-label]`, `[data-testid=agent-in-flight]`, `[data-testid=agent-prs] [data-pr][data-pr-state]` |
| Deploys | `[data-testid=rollback-count][data-rollbacks]`, `[data-testid=deploy-entry]`, `[data-testid=deploy-commit]` |
| Alarms | `[data-alarm=<name>][data-alarm-state=OK|ALARM|INSUFFICIENT_DATA]` |
| Feature flags | `[data-flag=<name>][data-flag-enabled=true|false]`, toggle `[data-testid=flag-toggle-<name>]` |
| Load generator | `[data-testid=load-state][data-load-running]`, `[data-testid=load-minutes]`, `[data-testid=load-start-normal]`, `load-start-peak`, `load-start-spike`, `load-start-soak`, `load-stop` |
| Scoreboard | `[data-testid=scoreboard] [data-score-row=<id>][data-score-pass]`, `[data-testid=ci-jobs] [data-ci-job]`, `[data-testid=scoreboard-saved]`, `[data-testid=past-runs] [data-past-run]`, `[data-testid=step-command]` |
| Live traffic (Tour) | `[data-testid=live-traffic]` |
| Cost | `[data-testid=cost-mtd]`, `[data-testid=cost-today]` |
| Event log | `[data-testid=event-log] [data-event-kind=<kind>]`; sources `[data-source][data-source-ok]` |
| Scenario library | `[data-testid=scenario-library] [data-scenario=<id>]` (links) |
| Run | `[data-testid=run][data-run-state=idle|running|completed|failed]` with `data-active-step`, `data-focus-step`; badge `[data-testid=run-state]`; `[data-testid=run-locked]` when another scenario holds the lock |
| Steps | `[data-testid=steps] [data-step=<id>][data-step-state=pending|ready|running|waiting|done|failed|timedout|locked]` (also `data-testid=step-<id>`), output `[data-testid=step-output-<id>]` |
| Current step card | `[data-testid=narration][data-step][data-step-state]`, `[data-testid=step-title]`, `[data-testid=step-status]`, `[data-testid=wait-elapsed]`, `[data-wait-satisfied]`, `[data-testid=step-message]` |
| Step action button | `[data-testid=step-action][data-step-action=<id>]`, id `action-<scenario>-<step>` |
| Keep waiting | `[data-testid=step-extend]` |
| Reset | `[data-testid=scenario-reset]` (asks for confirmation: accept the dialog) |
| Browse steps | `[data-testid=view-back]`, `view-next`, `view-follow` |
| Run links | `[data-testid=run-issue]`, `[data-testid=run-pr]`; log `[data-testid=run-log]`; live view `[data-testid=live-view]` |
| Tour stages | `[data-stage=<stage>][data-stage-state]` |
| Where this is running | `[data-testid=where-running] [data-node=<id>][data-node-on=true|false]` |
| Explainers | any `[data-explain=<id>]` opens `#explainer[data-testid=explainer-drawer][data-open=true][data-explain-id=<id>]`; `[data-testid=explainer-title]`, `explainer-what`, `explainer-azure`, `explainer-link`, close `[data-testid=explainer-close]` or Escape; `[data-testid=explain-current]` on the step card |
| Architecture | `[data-testid=architecture]`, `[data-testid=architecture-diagram]`, `[data-zone]`, `[data-component]` |
| Toast | `#toast[data-testid=toast][data-kind=info|error]` |

Live regions (`[data-poll-url]`, with `data-poll-at` set after each refresh) are re-rendered every 3 s (Present) or
`STATUS_REFRESH_SECONDS` (Status), so re-query elements rather than holding handles.

JSON API (same auth): `GET /api/whoami`, `/api/run`, `/api/events?count=`, `/api/status`, `/api/scenarios`;
`POST` (header `X-Console-Action: 1`) `/api/scenarios/{id}/start`, `/steps/{step}/action`, `/steps/{step}/extend`,
`/reset`, `/api/flags/{name}` `{ "enabled": true }`, `/api/load/start` `{ "scenario", "durationMinutes" }`, `/api/load/stop`.

## Tests

`tests/Orders.Console.Tests` (xUnit v3): step engine (advance on events, correlation, timeouts and keep waiting,
fail conditions, reset, lock), matching and scenario validation, auth (token handler, Google domain check,
401s, anonymous `/health/live`) through `WebApplicationFactory`, the fake world, and every shipped scenario run
to completion against the fakes.
