# 16. Flaky tests are found nightly and quarantined, never retried

- Date: 2026-11-10
- Status: Accepted

## Decision

A nightly workflow runs the unit and integration tests 5 times on main and opens or updates one `flaky` issue per
test that both passed and failed. A flaky test is quarantined within a day with `[Trait("Category", "Quarantine")]`,
which keeps it out of every required CI job but still runs it nightly. PR rules allow a quarantine only for a test
with an open `flaky` issue, and reject skips, `Explicit`, `#if false` and deleted tests. Retrying until green is not
allowed.

## Why

Retries hide real concurrency bugs and teach agents that red can be ignored. Quarantine keeps PRs unblocked while
keeping the test visible, and tying it to the nightly evidence stops it being used as a skip.
