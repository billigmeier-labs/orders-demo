# 15. Agent run limits: a FIFO queue, a cost cap, two strikes

- Date: 2026-11-10
- Status: Accepted

## Decision

At most 3 agent runs work at once. A `slot` job at the start of the Agent workflow waits until fewer than 3 older
Agent runs are unfinished. Each run has a timeout, a turn cap and a cost cap (Claude Code's `--max-budget-usd`: $5 to
implement an issue, $3 to address a review). A failed run labels the issue `agent:failed-once` and retries once; a
second failure labels it `needs-human` with a comment listing what was tried.

## Why

GitHub concurrency groups allow one running job per group and cancel all but the newest waiting run, so they can't
express "three at once" without dropping queued issues. Ordering by run ID gives every waiting run the same view of
the queue, so the cap holds without a lock. The cost cap bounds a runaway run; two strikes stops an agent from
burning money on an issue it can't do.
