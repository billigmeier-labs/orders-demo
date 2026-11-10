# Orders demo task runner. Run `just` to list recipes.
#
# scripts/dotnet calls `dotnet` directly when it is installed (devcontainer, agents) and otherwise
# runs it inside the devcontainer's .NET SDK image. CI forces the image (DOTNET_IN_DOCKER=1), so a
# laptop, an agent and CI build with the same SDK and run the same recipes.

set shell := ["bash", "-euo", "pipefail", "-c"]

dotnet := "./scripts/dotnet"
configuration := "Release"
postgres_image := "postgres:17-alpine"
api_port := "8080"
db_port := "5432"
db_container := "orders-dev-db"
e2e_port := "8088"
console_url := "https://console.demo.joshb.app"
# Where the API finds the dev Postgres when dotnet runs natively (the devcontainer sets host.docker.internal)
dev_db_host := env("DEV_DB_HOST", "localhost")
# `dotnet test` runs in Microsoft.Testing.Platform mode (global.json), so its options are xUnit's and the platform's.
# Tests tagged [Trait("Category", "Quarantine")] are flaky and left out of every required run (AGENTS.md).
test_filter := "--filter-not-trait 'Category=Quarantine'"
coverage_dir := "artifacts/coverage"
# coverlet.MTP writes one lcov file per test project; tests/testconfig.json says what is measured.
coverage_args := "--coverlet --results-directory " + coverage_dir
gitleaks_image := "zricethezav/gitleaks:v8.24.3"
trivy_image := "aquasec/trivy:0.76.1"
api_image := "orders-api:ci"
# Where this branch left main: what coverage-gate, pr-rules and risk-tier compare against by default
main_base := `git merge-base origin/main HEAD 2>/dev/null || git rev-parse HEAD 2>/dev/null || echo HEAD`

# List recipes
default:
    @just --list

# One command from a fresh clone to a working API: tools, packages, Postgres, migrations, seed data (idempotent)
setup: restore db db-prepare
    @echo "Ready. \`just dev\` runs the API on http://localhost:{{ api_port }} with hot reload."

# Time `just setup` and report setup-seconds=<n> (CI runs it on a fresh runner; agents start from scratch every task)
setup-time target="180":
    #!/usr/bin/env bash
    set -euo pipefail
    start=$(date +%s)
    just setup
    secs=$(( $(date +%s) - start ))
    echo "setup-seconds=$secs (target {{ target }})"
    if [ -n "${GITHUB_ACTIONS:-}" ]; then
        echo "setup-seconds=$secs" >> "$GITHUB_STEP_SUMMARY"
        echo "::notice title=setup-seconds::$secs"
        [ "$secs" -le {{ target }} ] || echo "::warning title=Slow setup::just setup took ${secs}s on a fresh runner (target {{ target }}s)."
    fi

# Restore tools and packages (updates lock files when packages change)
restore:
    {{ dotnet }} tool restore
    {{ dotnet }} restore Orders.slnx

# Start the dev Postgres on :5432 (keeps its data in the orders-dev-db volume; no-op when running)
db:
    #!/usr/bin/env bash
    set -euo pipefail
    state=$(docker inspect -f '{{{{.State.Running}}' {{ db_container }} 2>/dev/null || echo missing)
    case "$state" in
        true) ;;
        false) docker start {{ db_container }} >/dev/null ;;
        *) docker run -d --name {{ db_container }} -p {{ db_port }}:5432 -v orders-dev-db:/var/lib/postgresql/data \
               -e POSTGRES_USER=orders -e POSTGRES_PASSWORD=orders -e POSTGRES_DB=orders {{ postgres_image }} >/dev/null ;;
    esac
    for _ in $(seq 1 60); do docker exec {{ db_container }} pg_isready -h 127.0.0.1 -U orders -q 2>/dev/null && break; sleep 1; done
    # Over TCP: during first-time init a temporary server answers on the Unix socket only, then restarts.
    docker exec {{ db_container }} pg_isready -h 127.0.0.1 -U orders -q || { docker logs {{ db_container }} | tail -20; echo "Postgres didn't start." >&2; exit 1; }
    echo "Postgres is up on :{{ db_port }}."

# Delete the dev database and its data (the next `just setup` recreates and seeds it)
db-reset:
    docker rm -f {{ db_container }} >/dev/null 2>&1 || true
    docker volume rm orders-dev-db >/dev/null 2>&1 || true
    @echo "Dev database removed. Run \`just setup\`."

# Apply migrations and seed an empty dev database: starts the API once with MIGRATE_ON_STARTUP and SEED_DATA, then stops it
db-prepare:
    #!/usr/bin/env bash
    set -euo pipefail
    log=$(mktemp)
    export ASPNETCORE_ENVIRONMENT=Development MIGRATE_ON_STARTUP=true SEED_DATA=true
    export APP_VERSION=dev APP_COMMIT=local APP_ENVIRONMENT=local
    # Port 0: any free port, so this works while `just dev` is running.
    export ASPNETCORE_URLS=http://127.0.0.1:0
    db_host={{ dev_db_host }}
    if [ "${DOTNET_IN_DOCKER:-}" = 1 ] || ! command -v dotnet >/dev/null 2>&1; then
        db_host=host.docker.internal
        export DOTNET_DOCKER_ARGS="--name orders-setup -e ASPNETCORE_URLS -e ASPNETCORE_ENVIRONMENT -e MIGRATE_ON_STARTUP -e SEED_DATA -e APP_VERSION -e APP_COMMIT -e APP_ENVIRONMENT -e ConnectionStrings__Orders"
    fi
    export ConnectionStrings__Orders="Host=$db_host;Port={{ db_port }};Database=orders;Username=orders;Password=orders"
    docker rm -f orders-setup >/dev/null 2>&1 || true
    {{ dotnet }} run --project src/Orders.Api -c {{ configuration }} --no-launch-profile </dev/null >"$log" 2>&1 &
    pid=$!
    stop() { kill "$pid" 2>/dev/null || true; docker rm -f orders-setup >/dev/null 2>&1 || true; wait "$pid" 2>/dev/null || true; }
    trap stop EXIT
    # The API migrates and seeds before it starts listening, so "Application started" means the database is ready.
    for _ in $(seq 1 300); do
        grep -q "Application started" "$log" && { echo "Database migrated and seeded."; exit 0; }
        kill -0 "$pid" 2>/dev/null || break
        sleep 1
    done
    tail -40 "$log"
    echo "The API didn't finish migrating and seeding (log above). Is Postgres up? Try \`just db-reset\` then \`just setup\`." >&2
    exit 1

# Build everything, warnings as errors, lock files enforced
build:
    {{ dotnet }} build Orders.slnx -c {{ configuration }} -p:RestoreLockedMode=true

# Unit tests with coverage (fast, no Docker)
test:
    rm -rf {{ coverage_dir }}
    {{ dotnet }} test --project tests/Orders.UnitTests -c {{ configuration }} {{ test_filter }} {{ coverage_args }}

# Unit + integration tests with coverage (needs Docker)
test-all:
    rm -rf {{ coverage_dir }}
    {{ dotnet }} test --solution Orders.slnx -c {{ configuration }} {{ test_filter }} {{ coverage_args }}

# Every test, quarantined ones too, `runs` times; TRX results in artifacts/test-runs (the nightly flaky-test hunt)
test-repeat runs="5":
    #!/usr/bin/env bash
    set -euo pipefail
    rm -rf artifacts/test-runs
    {{ dotnet }} build Orders.slnx -c {{ configuration }} -p:RestoreLockedMode=true
    for i in $(seq 1 {{ runs }}); do
        echo "== Run $i of {{ runs }}"
        {{ dotnet }} test --project tests/Orders.UnitTests -c {{ configuration }} --no-build --results-directory artifacts/test-runs --report-xunit-trx --report-xunit-trx-filename unit-$i.trx || true
        {{ dotnet }} test --project tests/Orders.IntegrationTests -c {{ configuration }} --no-build --results-directory artifacts/test-runs --report-xunit-trx --report-xunit-trx-filename integration-$i.trx || true
    done
    # Failures are judged across runs (a test that always fails isn't flaky, it's broken): see scripts/flaky-report.
    scripts/flaky-report artifacts/test-runs

# Check that the lines this branch adds or changes are covered (run after `just test-all`)
coverage-gate base=main_base:
    scripts/coverage-gate {{ base }} {{ coverage_dir }}

# PR rules: no skipped or deleted tests, thresholds not lowered (+ title and linked issue when PR_* env vars are set)
pr-rules base=main_base:
    scripts/pr-rules {{ base }}

# Risk tier from CODEOWNERS for the changes since base: prints low or high
risk-tier base=main_base:
    @scripts/risk-tier {{ base }}

# Check formatting and style
lint:
    {{ dotnet }} format Orders.slnx --verify-no-changes

# Fix formatting and style
format:
    {{ dotnet }} format Orders.slnx

# Secret scan, vulnerable packages, and the API image scan
security: secrets vulnerable image-scan

# Secret scan of this branch's whole history (gitleaks; other branches are scanned on their own PRs)
secrets:
    #!/usr/bin/env bash
    set -euo pipefail
    # Same paths inside the container, plus the shared git dir, so it also works in a git worktree.
    common=$(cd "$(git rev-parse --git-common-dir)" && pwd)
    if ! docker run --rm -v "$PWD:$PWD" -v "$common:$common" -w "$PWD" {{ gitleaks_image }} git "$PWD" --redact --no-banner --log-opts=HEAD --config .gitleaks.toml; then
        echo "Fix: remove the secret (rotate it if it was real: it's in git history now). If it's a false positive," >&2
        echo "     add its fingerprint (shown above) to .gitleaksignore, or a path allowlist to .gitleaks.toml; a human reviews both." >&2
        exit 1
    fi

# NuGet packages with known high or critical vulnerabilities, direct or transitive
vulnerable:
    {{ dotnet }} restore Orders.slnx >/dev/null
    mkdir -p artifacts
    {{ dotnet }} list Orders.slnx package --vulnerable --include-transitive > artifacts/vulnerable.txt
    scripts/vulnerable-packages artifacts/vulnerable.txt

# Build the API image as orders-api:ci (uses the GitHub Actions layer cache in CI)
image platform="":
    #!/usr/bin/env bash
    set -euo pipefail
    args=()
    if [ -n "${ACTIONS_RUNTIME_TOKEN:-}" ]; then
        args+=(--cache-from type=gha,scope=api-ci --cache-to type=gha,mode=max,scope=api-ci)
    fi
    [ -z "{{ platform }}" ] || args+=(--platform "{{ platform }}")
    # --pull: scan the base image as it is today, not a stale local copy.
    docker buildx build --pull "${args[@]}" -f src/Orders.Api/Dockerfile -t {{ api_image }} --load .

# Build the API image and fail on HIGH or CRITICAL vulnerabilities that have a fix (Trivy)
image-scan: image
    #!/usr/bin/env bash
    set -euo pipefail
    mkdir -p .cache/trivy
    sock=/var/run/docker.sock
    if ! docker run --rm --user "$(id -u):$(id -g)" --group-add "$(stat -c %g "$sock")" -v "$sock:$sock" \
            -v "$PWD/.cache/trivy:/cache" -v "$PWD/.trivyignore.yaml:/trivyignore.yaml:ro" \
            {{ trivy_image }} image --cache-dir /cache --quiet --ignorefile /trivyignore.yaml \
            --severity HIGH,CRITICAL --ignore-unfixed --exit-code 1 --format table {{ api_image }}; then
        echo "Fix: the table above lists each vulnerable package and the version that fixes it." >&2
        echo "     OS packages: bump the base image tag in src/Orders.Api/Dockerfile (Dependabot does this weekly)." >&2
        echo "     NuGet packages: bump the version in Directory.Packages.props, then \`just restore\`." >&2
        echo "     Can't be fixed yet: add it to .trivyignore.yaml with a reason and an expiry (a human reviews it)." >&2
        exit 1
    fi
    echo "Image scan passed: no fixable HIGH or CRITICAL vulnerabilities in {{ api_image }}."

# Build and push the release images for a commit (the merge-queue job; needs AWS credentials)
release-images registry sha:
    scripts/release-images {{ registry }} {{ sha }}

# Everything PR CI runs, in the same order (pr-rules also checks title and issue link when PR_* are set)
ci: lint build test-all coverage-gate pr-rules security

# Mutation testing of the API with Stryker.NET (slow; report in StrykerOutput/<date>/reports)
mutation *args:
    {{ dotnet }} tool restore
    {{ dotnet }} tool run dotnet-stryker --config-file stryker-config.json {{ args }}

# Path of the newest Stryker JSON report (and `report=<path>` for GitHub Actions)
mutation-report:
    #!/usr/bin/env bash
    set -euo pipefail
    report=$(ls -t StrykerOutput/*/reports/mutation-report.json 2>/dev/null | head -1 || true)
    [ -n "$report" ] || { echo "No Stryker report in StrykerOutput/. The Stryker run failed before scoring; its log has the error." >&2; exit 1; }
    echo "$report"
    [ -z "${GITHUB_OUTPUT:-}" ] || echo "report=$report" >> "$GITHUB_OUTPUT"

# Add an EF Core migration, e.g. `just migration AddOrderNotes`
migration name:
    {{ dotnet }} tool restore
    {{ dotnet }} tool run dotnet-ef migrations add {{ name }} --project src/Orders.Api --output-dir Data/Migrations

# Run the API with hot reload (dotnet watch) on :8080 against the dev Postgres; FeatureFlags__* env vars pass through
dev: db
    #!/usr/bin/env bash
    set -euo pipefail
    trap 'docker rm -f orders-dev-api >/dev/null 2>&1 || true' EXIT
    echo "API on http://localhost:{{ api_port }} (edits reload automatically; Ctrl-C stops it, Postgres keeps running)"
    export ASPNETCORE_ENVIRONMENT=Development MIGRATE_ON_STARTUP=true SEED_DATA=true
    export APP_VERSION=dev APP_COMMIT=local APP_ENVIRONMENT=local
    export DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true
    db_host={{ dev_db_host }}
    if command -v dotnet >/dev/null 2>&1; then
        export ASPNETCORE_URLS=http://localhost:{{ api_port }}
    else
        # The API runs in the SDK container and reaches Postgres through the host.
        db_host=host.docker.internal
        export ASPNETCORE_URLS=http://+:8080
        export DOTNET_DOCKER_ARGS="--name orders-dev-api -p {{ api_port }}:8080 -e ASPNETCORE_URLS -e ASPNETCORE_ENVIRONMENT -e MIGRATE_ON_STARTUP -e SEED_DATA -e APP_VERSION -e APP_COMMIT -e APP_ENVIRONMENT -e ConnectionStrings__Orders -e DOTNET_WATCH_RESTART_ON_RUDE_EDIT"
        # Feature flags set in the calling shell, e.g. `FeatureFlags__searchV2=true just dev`
        for flag in $(compgen -e | grep '^FeatureFlags__' || true); do DOTNET_DOCKER_ARGS+=" -e $flag"; done
    fi
    export ConnectionStrings__Orders="Host=$db_host;Port={{ db_port }};Database=orders;Username=orders;Password=orders"
    {{ dotnet }} watch --project src/Orders.Api run --no-launch-profile

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
