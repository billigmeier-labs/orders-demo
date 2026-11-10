# 13. CI runs dotnet in the devcontainer's SDK image

- Date: 2026-11-10
- Status: Accepted

## Decision

One pinned .NET SDK image (the `FROM` line of `.devcontainer/Dockerfile`) is used everywhere. The devcontainer is
built from it, `scripts/dotnet` runs it on machines without an SDK, and CI sets `DOTNET_IN_DOCKER=1` so every
`dotnet` command in every job runs in it through `scripts/dotnet`. CI does not use job-level `container:`.

## Why

The spec wants laptop and CI to be the same container and the same commands. Running each `dotnet` command in the
image from the runner keeps the runner's Docker socket and network as they are, so Testcontainers and `just setup`
start sibling containers exactly as they do on a laptop (`host.docker.internal`), with no Docker-in-Docker. A
job-level `container:` would need the socket mounted and a different way to reach sibling containers' ports, and
would pull a larger image. The SDK image pull adds seconds; the NuGet cache (keyed on the lock files) is mounted
from `.cache/nuget`, so restores stay warm. Dependabot bumps the image tag in one file.
