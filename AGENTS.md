# AGENTS.md

Rules for any coding agent (and person) working in this repo. Read this and `docs/contracts.md` before changing anything.

## What this repo is

A demo of an agent-first delivery workflow: an orders API in .NET 10, deployed to AWS, where AI agents pick up issues, open PRs, and ship through automated checks. Names and shapes live in `docs/contracts.md`; decisions in `docs/adr/` (indexed in `docs/decisions.md`).

## Commands (always use these, never ad-hoc equivalents)

| Command | Does |
| --- | --- |
| `just setup` | From a fresh clone to a working API: tools, packages, Postgres, migrations, seed data. Unattended and safe to re-run. |
| `just dev` | The API on http://localhost:8080 with hot reload (`dotnet watch`) against the setup database |
| `just build` | Build everything, warnings as errors, lock files enforced |
| `just test` | Unit tests with coverage (fast, no Docker) |
| `just test-all` | Unit + integration tests with coverage (needs Docker) |
| `just lint` / `just format` | Check / fix formatting and style |
| `just coverage-gate` | Are the lines you added or changed covered? (after `just test-all`) |
| `just pr-rules` | No skipped or deleted tests, thresholds not lowered |
| `just security` | Secret scan, vulnerable packages, API image scan |
| `just ci` | Everything PR CI runs, in order. Must pass before you open a PR. |
| `just db-reset` | Throw the dev database away (then `just setup`) |

`dotnet` runs from your PATH when it's installed (the devcontainer has it) and otherwise in the same SDK container CI uses (`.devcontainer/Dockerfile`). CI runs these same recipes, so a red check reproduces with `just ci`.

## How to work

1. Work from a GitHub issue. Your PR title is a Conventional Commit (`feat:`, `fix:`, `test:`, `chore:`, `docs:`) and the body starts with `Closes #<issue>`. PR rules fail without the link (only Dependabot, release-please and `demo/*` PRs are exempt).
2. Branch name: `agent/<issue>-<short-slug>`.
3. Keep PRs small: one issue, ideally under 400 changed lines.
4. Every behaviour change has tests. New endpoints get an integration test.
5. Run `just ci` and fix everything before opening the PR. Every check prints the file, the line, what's wrong and how to fix it; do what it says.
6. Database changes are EF Core migrations that work with both the old and new code (add first, remove later). You may create a new migration for your change (`just migration <Name>`); CODEOWNERS makes a person review it before it can merge.

## Risk tiers

Every PR gets a `risk:low` or `risk:high` label from the AI review (and `agent` when the agent opened it).

- **high**: the PR touches a path in `.github/CODEOWNERS`, or the review judges it could lose data, change auth or break clients. A human must approve it.
  The owned paths: the pipeline and rules (`.github/`, `AGENTS.md`, `justfile`, `demo.just`, `scripts/`, `lefthook.yml`, `.devcontainer/`, `.gitleaksignore`, `.trivyignore.yaml`), infrastructure and data (`infra/`, `src/Orders.Api/Data/Migrations/`), build settings and test thresholds (`Directory.Build.props`, `global.json`, `stryker-config.json`, `coverage-thresholds.json`, `tests/coverage.runsettings`), and sign-in (`src/Orders.Console/Auth/`).
- **low**: everything else. It merges on green checks, no human needed.

Issues carry the same tier (the task form asks for it; triage can raise it, never lower it).

## Tests, coverage and flaky tests

- **Coverage**: at least `changedLines`% (in `coverage-thresholds.json`) of the executable lines you add or change under `src/` must be run by a test, and overall coverage must stay above `overallLines`%. `just coverage-gate` lists every uncovered `file:line`.
- **Never skip or remove tests.** PR rules fail on a new `Skip =`, `Assert.Skip*`, `Explicit = true`, `#if false`, a commented-out `[Fact]`, a Playwright `test.skip(` / `.skip(` / `.fixme(` (only a `test.skip(settings.live, ...)` guard for production runs is allowed), a deleted test method, or a lowered threshold. They also fail on a new `[ExcludeFromCodeCoverage]` or `[Obsolete]` under `src/`, since coverage leaves that code out.
- **Flaky tests** (pass and fail on the same commit) are found by the nightly Flaky tests workflow, which opens a `flaky` issue for each. Quarantine one **within a day** if it can't be fixed right away: add `[Trait("Category", "Quarantine")]` to the test in a PR that links its `flaky` issue. Quarantined tests are left out of every required CI job but still run nightly. PR rules only accept a quarantine for a test with an open `flaky` issue. Then fix the cause and remove the trait.
- **Retry-until-green is not allowed**: no retry loops, retry attributes, re-running CI hoping it passes, or sleeps to dodge timing problems.

## What you must not do

- Don't edit anything `.github/CODEOWNERS` lists (see Risk tiers) or migrations you didn't create in this PR. Those need a human; if your task needs them, say so in the PR and stop.
- Don't skip, delete or weaken tests, or lower a threshold, to make CI pass.
- Don't add secrets, keys or tokens to the repo.
- Don't push to `main`.

## Limits on agent runs

At most 3 agent runs work at once (the rest queue). Each run has a time limit, a turn limit and a cost cap. If a run on an issue fails, it's retried once (`agent:failed-once`); a second failure hands the issue to a person (`needs-human`) with a comment saying what was tried.
