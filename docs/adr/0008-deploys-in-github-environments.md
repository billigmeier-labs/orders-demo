# 8. Deploys run in GitHub environments

- Date: 2026-11-09
- Status: Accepted

## Decision

Deploys run in GitHub environments; the deploy role trusts only those environments.

## Why

Issue-triggered workflows (where an AI runs commands) also run on `main`, so a ref-based trust let them deploy.
