# 1. The admin can push straight to main (intentional)

- Date: 2026-11-08, confirmed 2026-11-10
- Status: Accepted

## Decision

The `main` ruleset keeps an admin bypass with mode **always**. The repository admin (Josh, and Claude working for him)
may push directly to `main` and admin-merge PRs. This is used for:

- the greenfield build-out and maintenance of the repo itself,
- demo resets (`just demo reset` reverts the agents' last changes on `main` so scenarios can run again),
- break-glass fixes.

Everyone else, including the agents (`claude[bot]`, `orders-automation[bot]`) and Dependabot, goes through a PR, the
required checks and the merge queue.

## Why

It's a demo repo with one maintainer. Without the bypass the build-out deadlocks on self-approval (the admin is the only
code owner and can't approve their own PRs), and every demo reset would need a PR and a review. Josh reviewed the
alternative (bypass for pull requests only) on 2026-11-10 and chose to keep it.

## Consequences

- Most of `main`'s history is direct pushes. That's expected, not a finding: reviewers and verification runs should not
  report the admin bypass or direct admin pushes as a defect.
- On stage, say so plainly: the admin bypass exists for break-glass and demo resets; agents never have it.
