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

# Add an EF Core migration, e.g. `just migration AddOrderNotes`
migration name:
    {{ dotnet }} tool restore
    {{ dotnet }} tool run dotnet-ef migrations add {{ name }} --project src/Orders.Api --output-dir Data/Migrations

# Run the API on :8080 against a seeded Postgres container (Ctrl-C stops both)
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
    db_host=localhost
    if command -v dotnet >/dev/null 2>&1; then
        export ASPNETCORE_URLS=http://localhost:{{ api_port }}
    else
        # The API runs in the SDK container and reaches Postgres through the host.
        db_host=host.docker.internal
        export ASPNETCORE_URLS=http://+:8080
        export DOTNET_DOCKER_ARGS="--name orders-dev-api -p {{ api_port }}:8080 -e ASPNETCORE_URLS -e ASPNETCORE_ENVIRONMENT -e MIGRATE_ON_STARTUP -e SEED_DATA -e APP_VERSION -e APP_COMMIT -e APP_ENVIRONMENT -e ConnectionStrings__Orders"
    fi
    export ConnectionStrings__Orders="Host=$db_host;Port={{ db_port }};Database=orders;Username=orders;Password=orders"
    {{ dotnet }} run --project src/Orders.Api --no-launch-profile
