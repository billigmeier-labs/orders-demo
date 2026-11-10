# 14. Coverage gate on changed lines

- Date: 2026-11-10
- Status: Accepted

## Decision

Tests collect line coverage with coverlet (lcov). `scripts/coverage-gate` fails a PR when less than
`changedLines`% of the executable lines it adds or edits under `src/` are run by any test, or when overall coverage
falls below `overallLines`%. Both numbers live in `coverage-thresholds.json` (80 and 80 to start), which CODEOWNERS
protects and PR rules refuse to see lowered.

## Why

A gate on new lines is what "new and changed lines must be covered" means, and unlike a global percentage it can't
be met by old code. 80% passes every agent PR so far except one untested HTTP helper, so it's strict without being
noise. Unit and integration coverage are merged, because endpoint code is tested through integration tests. Plain
bash and awk over lcov, so it runs in the SDK image with no extra tools.
