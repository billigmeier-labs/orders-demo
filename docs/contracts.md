# Contracts

The names and shapes every part of this repo agrees on. Change them here first, then in code.

## Projects

| Path | What |
| --- | --- |
| `Orders.slnx` | The solution |
| `src/Orders.Api` | ASP.NET Core (.NET 10) orders API |
| `src/Orders.Console` | Demo console (ASP.NET Core), later phase |
| `src/Orders.LoadGenerator` | NBomber load scenarios (`just load <scenario> <minutes>`) |
| `tests/Orders.UnitTests` | xUnit, no I/O |
| `tests/Orders.IntegrationTests` | xUnit + Testcontainers PostgreSQL |
| `tests/Orders.E2E` | Playwright suite for the console, later phase |
| `infra/terraform/base`, `infra/terraform/env` | AWS, later phase |

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
| `GET /api/products/low-stock?threshold=` | Products with `stock < threshold` (default 5), lowest stock first, ties by `sku`; `threshold=0` gives an empty list; 400 problem details if `threshold` is negative or not a number |
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
| `OTEL_SERVICE_NAME` | Service name on traces (default `orders-api`); ECS sets `orders-api-prod`, `orders-api-staging`, `orders-api-preview` |

In ECS every API task (prod, staging, previews) runs an `otel-collector` sidecar (ADOT collector, non-essential) next to the API container; the API sends OTLP to `http://localhost:4317` and the collector forwards traces to X-Ray. Metrics stay on the ALB.

Feature flags: `searchV2` (ranked full-text search), `faultSlowSearch` (adds 2 s to `percent`% of searches), `faultFailOrders` (`percent`% of order creates return 500). A fault that fires logs a `FaultInjected` warning with the flag and percent.

Logs outside Development are JSON, one object per line, with `AppVersion`, `AppCommit`, `AppEnvironment` (and `TraceId` during a request) on every line.

## Load generator

Env `TARGET_BASE_URL` (the task definition points it at production, `https://demo.joshb.app`), `SCENARIO` (`normal` ≈ 5 req/s, `peak` ≈ 25, `spike` = normal with a 60 s burst to 60, `soak` = normal, default 30 min), `DURATION_MINUTES` (default 5). Journeys: browse products, search, view orders, create orders, advance order status. 5xx and no-response count as failures; 4xx (409 out of stock or a lost race) are expected. Ends with a summary per endpoint on stdout.

## GitHub

- Default branch `main`. Squash merge only. Merge queue on `main`.
- Required status checks on `main`: **`ci-ok`** and **`ai-review`** (later phase). `ci.yml` runs on `pull_request`, `merge_group` and `push` to `main`.
- Labels: `demo`, `agent`, `needs-human`, `incident`, `risk:low`, `risk:medium`, `risk:high`, `difficulty:low`, `difficulty:medium`, `difficulty:high`, `mutation`, `bypass:build`.
- Secrets (repo): `ANTHROPIC_API_KEY`, `AUTOMATION_TOKEN` (fine-grained PAT), `CONSOLE_VERIFICATION_TOKEN`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`. AWS access is OIDC, no keys.

## Images and hosts

- ECR repositories `orders-api`, `orders-console`, `orders-loadgen`.
- Release image tag: the commit SHA from the `merge_group` run (equal to the commit that lands on `main`). Preview tag: `pr-<N>`.
- Hosts: `demo.joshb.app` (prod), `staging.demo.joshb.app`, `pr-<N>.demo.joshb.app`, `console.demo.joshb.app`.

## Console events

The console reads GitHub and AWS directly; it records what it saw as events `{ at, kind, subject, detail, url }` with `kind` in `issue.opened, issue.labeled, issue.assigned, pr.opened, check.completed, pr.merged, deploy.started, deploy.succeeded, deploy.rolledback, alarm.alarm, alarm.ok, preview.up, preview.down`.
