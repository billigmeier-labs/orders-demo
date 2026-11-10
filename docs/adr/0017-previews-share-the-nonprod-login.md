# 17. Previews log in to the non-prod database as its master user (known limit)

- Date: 2026-11-10
- Status: Accepted

## Decision

Each PR preview gets its own database (`pr_<N>`) on the non-prod RDS instance, but connects with that instance's master
user, the same login staging uses. We don't create a per-PR database user.

## Why

It's a demo: staging and preview data are generated seed data. Josh accepted the limit on 2026-11-10 rather than add
per-PR users. Production is fully separate (its own instance, security group, secret and roles), and preview tasks can't
read the prod or console secrets.

## Consequences

Code in any same-repo PR (including an agent's) can read or change the staging database and other previews' databases.
Reviewers and verification runs should treat this as known, not a new finding. A real project would create a
`pr_<N>` user that owns only its database, with its own secret.
