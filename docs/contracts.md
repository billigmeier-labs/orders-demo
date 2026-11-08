# Contracts

The names and shapes every part of this repo agrees on. Change them here first, then in code.

## Projects

| Path | What |
| --- | --- |
| `Orders.slnx` | The solution |
| `src/Orders.Api` | ASP.NET Core (.NET 10) orders API |
| `src/Orders.Console` | Demo console (ASP.NET Core), later phase |
| `src/Orders.LoadGenerator` | NBomber load scenarios, later phase |
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
| `GET /api/products`, `GET /api/products/{id}`, `POST /api/products` | Products: `id, sku, name, price, stock` |
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
