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

# Put an environment back on the previous release now (no staging, no rebuild, no bake), e.g. `just rollback` or `just rollback staging`
rollback env="prod":
    #!/usr/bin/env bash
    set -euo pipefail
    export AWS_REGION=us-east-1 AWS_DEFAULT_REGION=us-east-1
    case "{{ env }}" in prod|staging) ;; *) echo "env must be prod or staging"; exit 1 ;; esac
    cluster=orders-demo svc="api-{{ env }}" family="orders-demo-api-{{ env }}"
    start=$(date +%s)
    image() { aws ecs describe-task-definition --task-definition "$1" --query 'taskDefinition.containerDefinitions[0].image' --output text; }
    service() { aws ecs describe-services --cluster "$cluster" --services "$svc" --query 'services[0]' --output json; }

    # Releases are revisions whose image tag is a commit SHA (Terraform's own revisions run :bootstrap).
    # The newest one is the release to undo; the target is the newest older revision with a different image.
    # Deciding from the newest release rather than from what runs makes a re-run (or a rollback after ECS
    # already rolled back by itself) a no-op instead of a step further back.
    newest="" newest_image="" target=""
    for td in $(aws ecs list-task-definitions --family-prefix "$family" --sort DESC --status ACTIVE --max-items 40 \
        --query 'taskDefinitionArns[]' --output text | tr '\t' '\n' | grep -E "/$family:[0-9]+$"); do
      img=$(image "$td")
      [[ "${img##*:}" =~ ^[0-9a-f]{40}$ ]] || continue
      if [ -z "$newest" ]; then newest=$td newest_image=$img; continue; fi
      if [ "$img" != "$newest_image" ]; then target=$td; break; fi
    done
    [ -n "$target" ] || { echo "No earlier release found for $svc."; exit 1; }
    target_image=$(image "$target")

    # Manual rollbacks skip the bake and the deployment alarms (an alarm still in ALARM from the bad version
    # would otherwise roll the rollback back); the service's configured values are restored afterwards
    # without starting another deployment. The bake length comes from the service's bake_minutes tag (Terraform).
    svc_json=$(service)
    bake=$(aws ecs list-tags-for-resource --resource-arn "$(jq -r .serviceArn <<<"$svc_json")" \
      --query "tags[?key=='bake_minutes'].value | [0]" --output text)
    [[ "$bake" =~ ^[0-9]+$ ]] || bake=$(jq -r '.deploymentConfiguration.bakeTimeInMinutes // 0' <<<"$svc_json")
    configured=$(jq -c --argjson bake "$bake" '.deploymentConfiguration | .bakeTimeInMinutes = $bake | .alarms.enable = true' <<<"$svc_json")
    fast=$(jq -c '.bakeTimeInMinutes = 0 | .alarms.enable = false' <<<"$configured")

    primary=$(jq -r '.deployments[] | select(.status == "PRIMARY") | .taskDefinition' <<<"$svc_json")
    if [ "$(image "$primary")" = "$target_image" ]; then
      echo "$svc already runs the previous release ${target_image##*:} (${target##*/}); not starting another rollback."
    else
      echo "Rolling $svc back: ${newest_image##*:} -> ${target_image##*:} (${target##*/})"
      aws ecs update-service --cluster "$cluster" --service "$svc" --task-definition "$target" \
        --deployment-configuration "$fast" --query 'service.serviceName' --output text >/dev/null
    fi

    # Wait for the target to be PRIMARY and COMPLETED (bake 0: as soon as the new tasks are healthy and the old drained).
    for _ in $(seq 1 120); do
      svc_json=$(service)
      dep=$(jq -c '.deployments[] | select(.status == "PRIMARY")' <<<"$svc_json")
      state=$(jq -r '.rolloutState' <<<"$dep")
      echo "$(date '+%-I:%M:%S %p') rollout=$state running=$(jq .runningCount <<<"$dep")/$(jq .desiredCount <<<"$dep")"
      if [ "$(image "$(jq -r .taskDefinition <<<"$dep")")" = "$target_image" ] && [ "$state" = COMPLETED ] \
         && [ "$(jq '.deployments | length' <<<"$svc_json")" = 1 ]; then
        break
      fi
      [ "$state" = FAILED ] && { echo "The rollback deployment failed: $(jq -r .rolloutStateReason <<<"$dep")"; exit 1; }
      sleep 5
    done
    [ "$state" = COMPLETED ] || { echo "Rollback did not finish in 10 minutes; re-run just rollback {{ env }} to keep waiting."; exit 1; }

    if [ "$(jq -c '.deploymentConfiguration | {b: .bakeTimeInMinutes, a: .alarms.enable}' <<<"$svc_json")" != "$(jq -c '{b: .bakeTimeInMinutes, a: .alarms.enable}' <<<"$configured")" ]; then
      aws ecs update-service --cluster "$cluster" --service "$svc" --deployment-configuration "$configured" \
        --query 'service.serviceName' --output text >/dev/null
      echo "Restored the $bake-minute bake and the deployment alarms."
    fi
    echo "$svc is on ${target_image##*:} after $(( $(date +%s) - start )) s. The next merge to main deploys again (just prod pause holds production)."

# AWS environment: `just env up`, `just env down`, `just env nuke`, `just env plan`, `just env status`, `just env leftovers`
mod env "infra/infra.just"

# Load scenarios on ECS: `just load start <scenario> --minutes <N>`, `just load stop`, `just load status`, `just load local ...`
mod load "load.just"

# Pause or resume production deploys: `just prod pause`, `just prod resume`
mod prod "infra/prod.just"

# Demo resets: `just demo reset` (or flags-off, bad-deploy, incident, cleanup)
mod demo "demo.just"
