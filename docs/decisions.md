# Decisions

Short log of choices made while building, newest first. Anything here can be revisited.

| Date | Decision | Why |
| --- | --- | --- |
| 2026-11-08 | Greenfield build commits straight to `main`; PRs are for agents, bots and demo scenarios | Josh's call; avoids self-approval deadlock while the repo is being built |
| 2026-11-08 | Agents act as Anthropic's Claude GitHub App via `anthropics/claude-code-action` | No private key to manage |
| 2026-11-08 | .NET builds on atlas run in the `mcr.microsoft.com/dotnet/sdk:10.0` container | Nothing installed on the host |
| 2026-11-08 | Release image is built in the merge-queue run and tagged with that SHA | Production runs exactly the bytes that were tested |
| 2026-11-08 | Dependabot, not Renovate | Built into GitHub; one less outside service to explain |
| 2026-11-08 | Public repo in a free org | Rulesets, merge queue, CodeQL and unlimited standard runners at no cost |
| 2026-11-09 | Workflow automation acts as its own GitHub App, `orders-automation[bot]` | Labels, triage and incident comments, issues and merges were showing as Josh (AI review and preview comments come from `github-actions[bot]`); the PAT stays only for the AWS → GitHub dispatch and the console (which acts for the presenter) |
| 2026-11-09 | Deploys run in GitHub environments; the deploy role trusts only those environments | Issue-triggered workflows (where an AI runs commands) also run on `main`, so a ref-based trust let them deploy |
| 2026-11-09 | AI-review override = an admin's `/override-ai-review` comment newer than the PR's last commit | The label alone could be added by anyone with triage access and survived new pushes |
| 2026-11-09 | The coding agent only sees issue comments from people with write access and our bots | Public repo: a stranger's comment was a prompt-injection path |
| 2026-11-09 | Preview images live in their own ECR repository; release tags are immutable | A PR could otherwise overwrite the image production runs |
| 2026-11-09 | `just rollback` puts prod straight back on the previous release | The spec's one-command rollback, without going through staging |
