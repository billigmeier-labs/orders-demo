# 18. The Claude GitHub App's broad permissions (known limit)

- Date: 2026-11-10
- Status: Accepted

## Decision

Agents push through Anthropic's Claude GitHub App (`claude[bot]`), installed on the org with broad permissions
(including workflows, webhooks and Actions). We keep it as is.

## Why

It's a demo with one maintainer. Josh accepted the limit on 2026-11-10 rather than narrow the install or switch agents to
a narrower token (which would change the PR author shown in the demo).

## Consequences

A prompt-injected agent could push a workflow change on its own branch, and that PR's workflow would run with the repo's
secrets before review. Mitigations in place: agents see only trusted issue and review text, privileged jobs run the base
branch's scripts, `.github/` changes can't merge without the code owner, and repo secrets are down to the Claude key and
the automation app. Reviewers and verification runs should treat this as known, not a new finding.
