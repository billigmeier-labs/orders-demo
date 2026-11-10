# 9. AI-review override is an admin comment

- Date: 2026-11-09
- Status: Accepted

## Decision

AI-review override = an admin's `/override-ai-review` comment newer than the PR's last commit.

## Why

The label alone could be added by anyone with triage access and survived new pushes.
