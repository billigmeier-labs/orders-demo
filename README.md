# orders-demo

A small orders API in .NET 10 (minimal APIs, EF Core, PostgreSQL), used to demo an agent-first delivery workflow: AI agents pick up issues, open PRs and ship through automated checks.

- [AGENTS.md](AGENTS.md): how to work in this repo (people and agents)
- [docs/contracts.md](docs/contracts.md): endpoints, names and configuration
- [docs/decisions.md](docs/decisions.md): decision log

## Run it

You need [just](https://just.systems) and Docker. The .NET 10 SDK is optional: without it, every recipe runs `dotnet` in the `mcr.microsoft.com/dotnet/sdk:10.0` container (see `scripts/dotnet`). The devcontainer has everything.

```sh
just setup   # restore tools and packages
just ci      # lint, build, unit + integration tests: exactly what CI runs
just dev     # API on http://localhost:8080 with a seeded Postgres container
```

Then try `curl localhost:8080/health/ready` or `curl localhost:8080/api/orders`. `just` lists every recipe.

## Layout

| Path | What |
| --- | --- |
| `src/Orders.Api/Domain` | Customers, products, orders and their rules (stock, status transitions) |
| `src/Orders.Api/Features` | Endpoints, one folder per feature |
| `src/Orders.Api/Data` | `DbContext`, migrations, seed data |
| `tests/Orders.UnitTests` | Domain rules, no I/O |
| `tests/Orders.IntegrationTests` | Every endpoint against a real Postgres (Testcontainers) |

## Container image

`src/Orders.Api/Dockerfile` builds the API for `linux/amd64` or `linux/arm64` (non-root, port 8080). The image also contains a self-contained EF Core migration bundle at `/app/efbundle`.

```sh
docker build -f src/Orders.Api/Dockerfile -t orders-api .
```

The connection string may leave out the password; Npgsql then reads `PGPASSWORD`. To apply migrations, run the same image with the bundle as its command:

```sh
docker run --rm -e ConnectionStrings__Orders="Host=...;Database=orders;Username=orders" -e PGPASSWORD=... orders-api ./efbundle
```

The bundle reads `ConnectionStrings__Orders` like the app does. To pass the string explicitly instead: `sh -c './efbundle --connection "$ConnectionStrings__Orders"'`.

The API applies migrations itself only when `MIGRATE_ON_STARTUP=true` (local dev and tests); deployed environments run the bundle.
