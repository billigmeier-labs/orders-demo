# Contracts

The names and shapes every part of this repo agrees on. Change them here first, then in code.

## Projects

| Path | What |
| --- | --- |
| `Orders.slnx` | The solution |
| `src/Orders.Api` | ASP.NET Core (.NET 10) orders API |
| `src/Orders.Console` | Demo console (ASP.NET Core): Status, Present (Tour and scenarios), Architecture |
| `src/Orders.LoadGenerator` | NBomber load scenarios (`just load start <scenario> --minutes <N>` on ECS, `just load local <scenario> <minutes>` here) |
| `tests/Orders.UnitTests` | xUnit, no I/O |
| `tests/Orders.IntegrationTests` | xUnit + Testcontainers PostgreSQL |
| `tests/Orders.E2E` | Playwright suite for the console (`just e2e` fake mode, `just e2e-live` against AWS) |
| `infra/terraform/base`, `infra/terraform/env` | AWS (`just env up` / `down`; see `infra/README.md`) |

## Orders API

Base path `/api`. JSON, camelCase. Errors are RFC 7807 problem details. IDs are GUIDs. Money is `decimal` with 2 places, currency USD implied.

| Method and path | Purpose |
| --- | --- |
| `GET /health/live` | Process is up (no DB) |
| `GET /health/ready` | DB reachable |
| `GET /api/version` | `{ version, commit, environment }` from env vars `APP_VERSION`, `APP_COMMIT`, `APP_ENVIRONMENT` |
| `GET /api/customers`, `GET /api/customers/{id}`, `POST /api/customers` | Customers: `id, name, email, createdAt` |
| `GET /api/customers/{id}/orders?page=&pageSize=` | A customer's orders, newest first, same paged shape as `GET /api/orders` (default 20, max 100); 404 if the customer doesn't exist |
| `GET /api/products`, `GET /api/products/{id}`, `POST /api/products` | Products: `id, sku, name, price, stock` |
| `POST /api/products/{id}/restock` | `{ quantity }` (1–10000) adds to stock atomically and returns the product; 400 if out of range, 404 if unknown |
| `GET /api/orders?status=&customerId=&page=&pageSize=` | List orders, newest first, paged (default 20, max 100) |
| `GET /api/orders/{id}` | One order with lines and total |
| `POST /api/orders` | Create: `{ customerId, lines: [{ productId, quantity }] }`; unit price copied from product; reduces stock; 409 if not enough stock |
| `POST /api/orders/{id}/status` | `{ status }`; allowed transitions only (below); 409 otherwise |
| `GET /api/search?q=` | Products by name or SKU and orders by id prefix or customer name, max 20 each |

Order status: `Pending → Paid → Shipped → Delivered`; `Pending` or `Paid → Cancelled` (restocks).

Seed data (development, staging, preview, and prod since this is a demo): 20 customers, 50 products, 200 orders, deterministic.

## Configuration

| Env var | Meaning |
| --- | --- |
| `ConnectionStrings__Orders` | PostgreSQL connection string |
| `APP_VERSION`, `APP_COMMIT`, `APP_ENVIRONMENT` | Shown by `/api/version`, attached to every log line |
| `SEED_DATA` | `true` to seed on startup when empty |
| `APPCONFIG_APPLICATION`, `APPCONFIG_ENVIRONMENT`, `APPCONFIG_PROFILE` | Feature flags from AppConfig (polled every 15 s); unset means flags come from `FeatureFlags__<name>` (or `FeatureFlags__<name>__enabled` plus `FeatureFlags__<name>__percent`) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Turns on OpenTelemetry traces and metrics over OTLP; unset means off |
| `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE`, `OTEL_METRIC_EXPORT_INTERVAL` | ECS sets `delta` and `60000` on prod and staging (CloudWatch takes per-minute values) |
| `OTEL_METRICS_EXPORTER` | Previews set `none` (their collector takes traces only) |
| `OTEL_SERVICE_NAME` | Service name on traces (default `orders-api`); ECS sets `orders-api-prod`, `orders-api-staging`, `orders-api-preview` |

In ECS every API task (prod, staging, previews) runs an `otel-collector` sidecar (ADOT collector, non-essential) next to the API container; the API sends OTLP to `http://localhost:4317` and the collector forwards traces to X-Ray. In prod and staging it also sends the per-endpoint request metric to CloudWatch (EMF records in log group `/ecs/orders-demo/metrics`); previews send traces only. Alarms stay on the ALB metrics.

Per-endpoint metrics (what the API must export and what CloudWatch then has):

- The API exports ASP.NET Core's `http.server.request.duration` histogram (seconds; meter `Microsoft.AspNetCore.Hosting`) with its `http.route` attribute, plus an attribute **`status_class`** = `2xx`, `3xx`, `4xx` or `5xx` that the API adds itself (for example through `IHttpMetricsTagsFeature` after the response is written; the ADOT collector has no transform processor to derive it). Use a base-2 exponential histogram for that instrument so CloudWatch can compute percentiles. Other instruments are dropped by the collector.
- Resource attributes: `service.name` (from `OTEL_SERVICE_NAME`, e.g. `orders-api-prod`) and `deployment.environment.name` (`APP_ENVIRONMENT`: `prod`, `staging`).
- CloudWatch: namespace **`OrdersDemo`**, metric **`RequestDuration`** (seconds; `SampleCount` = requests, `p95` = latency, `status_class=5xx` = errors), dimension sets `[service, environment, endpoint, status_class]`, `[service, environment, endpoint]`, `[service, environment, status_class]`, `[service, environment]`, where `endpoint` is the route template (`/api/orders/{id}`).
- Also in `OrdersDemo`: `TaskStops` (dimension `group` = `service:api-prod` etc.), service tasks that crashed, failed to start or failed health checks.

Feature flags: `searchV2` (ranked full-text search), `faultSlowSearch` (adds 2 s to `percent`% of searches), `faultFailOrders` (`percent`% of order creates return 500). A fault that fires logs a `FaultInjected` warning with the flag and percent.

Logs outside Development are JSON, one object per line, with `AppVersion`, `AppCommit`, `AppEnvironment` (and `TraceId` during a request) on every line.

## Load generator

On ECS it is task definition `orders-demo-loadgen` (container `loadgen`, SSM `/orders-demo/loadgen/family`), started with `RunTask` by the console or `just load start`: started-by `orders-console`, tags `project=orders-demo`, `component=loadgen` (the console may stop only tasks with it) and `demo-run=<N>`, overrides `SCENARIO` and `DURATION_MINUTES`, subnets and security group from `/orders-demo/subnets` and `/orders-demo/task-security-group`, public IP on. `just load stop` stops every running task started by `orders-console`.

Env `TARGET_BASE_URL` (the task definition points it at production, `https://demo.joshb.app`), `SCENARIO` (`normal` ≈ 5 req/s, `peak` ≈ 25, `spike` = normal with a 60 s burst to 60, `soak` = normal, default 30 min), `DURATION_MINUTES` (default 5). Journeys: browse products, search, view orders, create orders, advance order status. 5xx and no-response count as failures; 4xx (409 out of stock or a lost race) are expected. Before the scenario starts it restocks every product with stock below 20 up to 100 (via `POST /api/products/{id}/restock`) and logs how many; a failed restock is logged and the run continues. Ends with a summary per endpoint on stdout.

## GitHub

- Default branch `main`. Squash merge only. Merge queue on `main`.
- Required status checks on `main`: **`ci-ok`** and **`ai-review`**, both pinned to GitHub Actions (app 15368) so no other app can report them. `ci.yml` runs on `pull_request`, `merge_group` and `push` to `main`.
- Labels: `demo`, `agent`, `needs-human`, `incident`, `risk:low`, `risk:medium`, `risk:high`, `difficulty:low`, `difficulty:medium`, `difficulty:high`, `mutation`, `flaky`, `agent:failed-once`, `bypass:build`. PRs get `risk:low` or `risk:high` (and `agent` for the agent's PRs) from the AI review.
- The nightly Mutation workflow reports its score as an annotation `::notice title=mutation-score::<pct>` (e.g. `72.4`); the console counts open `flaky` issues (opened by the nightly Flaky tests workflow, one per test, marker `<!-- flaky-test: <Namespace.Class.Method> -->`).
- Secrets (repo): `ANTHROPIC_API_KEY`, `AUTOMATION_APP_ID` + `AUTOMATION_APP_KEY` (the `orders-automation` GitHub App: workflows mint a short-lived token with `actions/create-github-app-token`, so labels, comments, issues and auto-merge show as `orders-automation[bot]`), `AUTOMATION_TOKEN` (fine-grained PAT, now only for the AWS alarm → `repository_dispatch` API destination and the console, which acts for the presenter), `CONSOLE_VERIFICATION_TOKEN`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`. AWS access is OIDC, no keys.
- GitHub environments (the AWS roles trust them, not branches): `staging` and `production` (deploy jobs, role `orders-demo-deploy`; main only), `release` (merge-queue release image job, deploy Prepare and `images.yml`, role `orders-demo-release`; main and `gh-readonly-queue/main/*`), `preview-reaper` (nightly reaper, role `orders-demo-preview`; main only). PR previews assume `orders-demo-preview` with the `pull_request` subject. The roles also pin the workflow file (`job_workflow_ref`, `infra/terraform/base/iam_github.tf`): add a new workflow there before it can assume a role.
- Production pause: `just prod pause` makes you a required reviewer on the `production` environment, so production deploy jobs (API and console) wait in Actions; `just prod resume` removes it and approves the waiting ones; `just prod status`.

## Images and hosts

- ECR repositories `orders-api`, `orders-console`, `orders-loadgen` (release images, tags immutable; `orders-loadgen:latest` is the one mutable tag), and `orders-api-preview` (PR images, mutable, expire after 7 days).
- Release image tag: the commit SHA from the `merge_group` run (equal to the commit that lands on `main`). Preview tag: `orders-api-preview:pr-<N>`.
- Hosts: `demo.joshb.app` (prod), `staging.demo.joshb.app`, `pr-<N>.demo.joshb.app`, `console.demo.joshb.app`.
- `images.yml` (workflow_dispatch, input `sha`) builds the three release images for a commit on main; `just env up` runs it because `just env down` deletes every image (the repositories stay).
- Previews: database `pr_<N>` on the non-prod instance, dropped when the PR closes (task `orders-demo-pr-dropdb`, SSM `/orders-demo/preview/dropdb-family`) and by the nightly reaper for any PR that is not open.

## Demo runs, tags and metrics history

- Every AWS resource is tagged `project=orders-demo` and `demo-run`: the run number `<N>` for everything `just env up` builds (and everything CI, previews and the console create: copy the tags or read `/orders-demo/demo-run`), `persistent` for what survives teardown (base layer: GitHub roles, ECR repositories, the counter, the metrics bucket). Previews add `preview=pr-<N>`, load generator tasks `component=loadgen`.
- SSM `/orders-demo/demo-run`: the current run N (`just env up` increments it for a fresh environment). The console reads it.
- SSM `/orders-demo/metrics-bucket`: S3 bucket (persistent) for per-run snapshots. The console writes **`runs/<N>.json`** (and may list and read `runs/`) so runs can be compared after teardown; its task role can do exactly that.
- Cost Explorer by tag needs `project` and `demo-run` activated as cost allocation tags (Billing console, once).

## Alarms and dashboards

Alarms are named `orders-demo-<service>-<kind>`; state changes go through EventBridge to `incident.yml` (`repository_dispatch` `alarm`), and the console lists them by the prefix `orders-demo-`.

| Alarm | For | Fires when | Rolls a deploy back |
| --- | --- | --- | --- |
| `-5xx` | api-prod, api-staging, console | more than 10 target 5xx in each of 2 minutes | yes |
| `-p95` | api-prod, api-staging, console | target response time p95 over 1.5 s for 2 minutes | yes |
| `-error-rate` | api-prod, api-staging | over 5 % of requests are 5xx for 2 minutes (minutes under 20 requests count as 0 %) | yes |
| `-unhealthy` | api-prod, api-staging | a target fails the ALB health check for 2 minutes | no |
| `-task-restarts` | api-prod, api-staging | a task crashed, failed to start or failed health checks in the last 5 minutes | no |

Dashboards: `orders-demo-prod`, `orders-demo-staging` (CloudWatch).

## Console events

The console reads GitHub and AWS directly; it records what it saw as events `{ at, kind, subject, detail, url }` with `kind` in `issue.opened, issue.labeled, issue.assigned, pr.opened, check.completed, pr.merged, deploy.started, deploy.succeeded, deploy.rolledback, alarm.alarm, alarm.ok, preview.up, preview.down`.
