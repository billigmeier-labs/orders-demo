# AGENTS.md

Rules for any coding agent (and person) working in this repo. Read this and `docs/contracts.md` before changing anything.

## What this repo is

A demo of an agent-first delivery workflow: an orders API in .NET 10, deployed to AWS, where AI agents pick up issues, open PRs, and ship through automated checks. Spec and names live in `docs/contracts.md`; decisions in `docs/decisions.md`.

## Commands (always use these, never ad-hoc equivalents)

| Command | Does |
| --- | --- |
| `just setup` | Restore tools and packages |
| `just build` | Build everything, warnings as errors |
| `just test` | Unit tests (fast, no Docker) |
| `just test-all` | Unit + integration tests (needs Docker) |
| `just lint` | `dotnet format --verify-no-changes` |
| `just format` | Fix formatting |
| `just ci` | Everything CI runs. Must pass before you open a PR. |
| `just dev` | Run the API with Postgres locally |

If `dotnet` isn't installed, the `just` recipes run it in the official .NET SDK container.

## How to work

1. Work from a GitHub issue. Your PR title is a Conventional Commit (`feat:`, `fix:`, `test:`, `chore:`, `docs:`) and the body says `Closes #<issue>`.
2. Branch name: `agent/<issue>-<short-slug>`.
3. Keep PRs small: one issue, ideally under 400 changed lines.
4. Every behaviour change has tests. New endpoints get an integration test.
5. Run `just ci` and fix everything before opening the PR.
6. Database changes are EF Core migrations that work with both the old and new code (add first, remove later). You may create a new migration for your change (`just migration <Name>`); CODEOWNERS makes a person review it before it can merge.

## What you must not do

- Don't edit anything `.github/CODEOWNERS` lists (`.github/`, `AGENTS.md`, `justfile`, `scripts/`, `infra/`, sign-in code, build settings, test thresholds) or migrations you didn't create in this PR. Those need a human; if your task needs them, say so in the PR and stop.
- Don't skip, delete or weaken tests to make CI pass.
- Don't add secrets, keys or tokens to the repo.
- Don't push to `main`.
