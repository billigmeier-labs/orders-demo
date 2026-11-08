# Orders demo task runner. Run `just` to list recipes.
#
# scripts/dotnet calls `dotnet` directly when it is installed (CI, devcontainer)
# and otherwise runs it inside the official .NET SDK container.

set shell := ["bash", "-euo", "pipefail", "-c"]

dotnet := "./scripts/dotnet"
configuration := "Release"
postgres_image := "postgres:17-alpine"
api_port := "8080"
db_port := "5432"
e2e_port := "8088"
console_url := "https://console.demo.joshb.app"
# Where the API finds the dev Postgres when dotnet runs natively (the devcontainer sets host.docker.internal)
dev_db_host := env("DEV_DB_HOST", "localhost")

# List recipes
default:
    @just --list

# Restore tools and packages (updates lock files when packages change)
setup:
    {{ dotnet }} tool restore
    {{ dotnet }} restore Orders.slnx

# Build everything, warnings as errors, lock files enforced
build:
    {{ dotnet }} build Orders.slnx -c {{ configuration }} -p:RestoreLockedMode=true

# Unit tests (fast, no Docker)
test:
    {{ dotnet }} test tests/Orders.UnitTests -c {{ configuration }}

# Unit + integration tests (needs Docker)
test-all:
    {{ dotnet }} test Orders.slnx -c {{ configuration }}

# Check formatting and style
lint:
    {{ dotnet }} format Orders.slnx --verify-no-changes

# Fix formatting and style
format:
    {{ dotnet }} format Orders.slnx

# Everything CI runs
ci: lint build test-all

# Mutation testing of the API with Stryker.NET (slow; report in StrykerOutput/<date>/reports)
mutation *args:
    {{ dotnet }} tool restore
    {{ dotnet }} tool run dotnet-stryker --config-file stryker-config.json {{ args }}

# Add an EF Core migration, e.g. `just migration AddOrderNotes`
migration name:
    {{ dotnet }} tool restore
    {{ dotnet }} tool run dotnet-ef migrations add {{ name }} --project src/Orders.Api --output-dir Data/Migrations

# Run the API on :8080 against a seeded Postgres container (Ctrl-C stops both); FeatureFlags__* env vars pass through
dev:
    #!/usr/bin/env bash
    set -euo pipefail
    docker rm -f orders-dev-db >/dev/null 2>&1 || true
    docker run -d --name orders-dev-db -p {{ db_port }}:5432 \
        -e POSTGRES_USER=orders -e POSTGRES_PASSWORD=orders -e POSTGRES_DB=orders \
        {{ postgres_image }} >/dev/null
    trap 'docker rm -f orders-dev-api orders-dev-db >/dev/null 2>&1 || true' EXIT
    until docker exec orders-dev-db pg_isready -U orders -q; do sleep 1; done
    echo "Postgres is up on :{{ db_port }}. API on http://localhost:{{ api_port }}"

    export ASPNETCORE_ENVIRONMENT=Development MIGRATE_ON_STARTUP=true SEED_DATA=true
    export APP_VERSION=dev APP_COMMIT=local APP_ENVIRONMENT=local
    db_host={{ dev_db_host }}
    if command -v dotnet >/dev/null 2>&1; then
        export ASPNETCORE_URLS=http://localhost:{{ api_port }}
    else
        # The API runs in the SDK container and reaches Postgres through the host.
        db_host=host.docker.internal
        export ASPNETCORE_URLS=http://+:8080
        export DOTNET_DOCKER_ARGS="--name orders-dev-api -p {{ api_port }}:8080 -e ASPNETCORE_URLS -e ASPNETCORE_ENVIRONMENT -e MIGRATE_ON_STARTUP -e SEED_DATA -e APP_VERSION -e APP_COMMIT -e APP_ENVIRONMENT -e ConnectionStrings__Orders"
        # Feature flags set in the calling shell, e.g. `FeatureFlags__searchV2=true just dev`
        for flag in $(compgen -e | grep '^FeatureFlags__' || true); do DOTNET_DOCKER_ARGS+=" -e $flag"; done
    fi
    export ConnectionStrings__Orders="Host=$db_host;Port={{ db_port }};Database=orders;Username=orders;Password=orders"
    {{ dotnet }} run --project src/Orders.Api --no-launch-profile

# Load generator against TARGET_BASE_URL (default the `just dev` API), e.g. `just load spike 5`
load scenario="normal" minutes="1":
    #!/usr/bin/env bash
    set -euo pipefail
    export SCENARIO={{ scenario }} DURATION_MINUTES={{ minutes }}
    export TARGET_BASE_URL="${TARGET_BASE_URL:-http://localhost:{{ api_port }}}"
    if ! command -v dotnet >/dev/null 2>&1; then
        # In the SDK container, localhost is the container: reach the host's port instead.
        TARGET_BASE_URL=$(sed -E 's#^(https?://)(localhost|127\.0\.0\.1)([:/]|$)#\1host.docker.internal\3#' <<<"$TARGET_BASE_URL")
        export DOTNET_DOCKER_ARGS="-e TARGET_BASE_URL -e SCENARIO -e DURATION_MINUTES"
    fi
    {{ dotnet }} run --project src/Orders.LoadGenerator -c {{ configuration }}

# Playwright suite against the console in fake mode, auth on (extra args go to `playwright test`)
e2e *args:
    #!/usr/bin/env bash
    set -euo pipefail
    docker build -f src/Orders.Console/Dockerfile -t orders-console:e2e .
    token=$(openssl rand -hex 24)
    docker rm -f orders-console-e2e >/dev/null 2>&1 || true
    trap 'docker rm -f orders-console-e2e >/dev/null 2>&1 || true' EXIT
    docker run -d --name orders-console-e2e -p 127.0.0.1:{{ e2e_port }}:8080 \
        -e CONSOLE_FAKES=true -e CONSOLE_VERIFICATION_TOKEN="$token" orders-console:e2e >/dev/null
    for _ in $(seq 1 60); do
        curl -fsS http://127.0.0.1:{{ e2e_port }}/health/live >/dev/null 2>&1 && break
        sleep 1
    done
    curl -fsS http://127.0.0.1:{{ e2e_port }}/health/live >/dev/null || { docker logs orders-console-e2e; exit 1; }
    E2E_MODE=fake CONSOLE_URL=http://localhost:{{ e2e_port }} CONSOLE_VERIFICATION_TOKEN="$token" \
        just _playwright {{ args }}

# Playwright suite in live mode against the real console (`just e2e-live --grep-invert @live-slow` for the quick part)
e2e-live *args:
    #!/usr/bin/env bash
    set -euo pipefail
    token_file="$HOME/.config/op/orders-demo-console-token"
    [ -r "$token_file" ] || { echo "Put the console verification token in $token_file" >&2; exit 1; }
    E2E_MODE=live CONSOLE_URL={{ console_url }} CONSOLE_VERIFICATION_TOKEN="$(tr -d '[:space:]' < "$token_file")" \
        just _playwright {{ args }}

# Runs tests/Orders.E2E in the official Playwright image matching its @playwright/test version
[private]
_playwright *args:
    #!/usr/bin/env bash
    set -euo pipefail
    version=$(sed -nE 's/.*"@playwright\/test": "([0-9.]+)".*/\1/p' tests/Orders.E2E/package.json)
    docker run --rm --network host --ipc host --init \
        --user "$(id -u):$(id -g)" -e HOME=/tmp -e npm_config_cache=/tmp/.npm -e CI=1 \
        -e E2E_MODE -e CONSOLE_URL -e CONSOLE_VERIFICATION_TOKEN \
        -v "$PWD/tests/Orders.E2E:/e2e" -w /e2e \
        "mcr.microsoft.com/playwright:v${version}-noble" \
        bash -c 'npm ci --no-audit --no-fund --loglevel=error && npx playwright test "$@"' playwright {{ args }}

# AWS environment: `just env up`, `just env down`, `just env nuke`, `just env plan`, `just env status`
mod env "infra/infra.just"
