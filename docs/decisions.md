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
| 2026-11-09 | Workflow automation acts as its own GitHub App, `orders-automation[bot]` | Labels, comments, issues and merges were showing as Josh; the PAT stays only for the AWS → GitHub dispatch and the console (which acts for the presenter) |
