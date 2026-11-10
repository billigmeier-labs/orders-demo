# Decisions

One short record per significant choice, in [docs/adr/](adr/). Add a new one with the next number (copy any
record's shape: decision, then why); anything here can be revisited by a later record.

| # | Date | Decision |
| --- | --- | --- |
| 1 | 2026-11-08 | [The admin can push straight to main (intentional)](adr/0001-greenfield-commits-to-main.md) |
| 2 | 2026-11-08 | [Agents act as Anthropic's Claude GitHub App](adr/0002-agents-use-claude-github-app.md) |
| 3 | 2026-11-08 | [.NET builds run in the .NET SDK container](adr/0003-dotnet-in-sdk-container.md) |
| 4 | 2026-11-08 | [The release image is built in the merge queue](adr/0004-release-image-built-in-merge-queue.md) |
| 5 | 2026-11-08 | [Dependabot, not Renovate](adr/0005-dependabot-not-renovate.md) |
| 6 | 2026-11-08 | [Public repo in a free GitHub org](adr/0006-public-repo-free-org.md) |
| 7 | 2026-11-09 | [Workflow automation acts as its own GitHub App](adr/0007-automation-github-app.md) |
| 8 | 2026-11-09 | [Deploys run in GitHub environments](adr/0008-deploys-in-github-environments.md) |
| 9 | 2026-11-09 | [AI-review override is an admin comment](adr/0009-admin-ai-review-override.md) |
| 10 | 2026-11-09 | [The coding agent sees only trusted comments](adr/0010-agent-sees-trusted-comments-only.md) |
| 11 | 2026-11-09 | [Preview images live in their own ECR repository](adr/0011-separate-preview-image-repo.md) |
| 12 | 2026-11-09 | [`just rollback` goes straight back to the previous release](adr/0012-one-command-rollback.md) |
| 13 | 2026-11-10 | [CI runs dotnet in the devcontainer's SDK image](adr/0013-ci-runs-in-the-devcontainer-sdk-image.md) |
| 14 | 2026-11-10 | [Coverage gate on changed lines](adr/0014-coverage-gate-on-changed-lines.md) |
| 15 | 2026-11-10 | [Agent run limits: a FIFO queue, a cost cap, two strikes](adr/0015-agent-run-limits.md) |
| 16 | 2026-11-10 | [Flaky tests are found nightly and quarantined, never retried](adr/0016-flaky-tests-quarantine.md) |
| 17 | 2026-11-10 | [Previews log in to the non-prod database as its master user (known limit)](adr/0017-previews-share-the-nonprod-login.md) |
| 18 | 2026-11-10 | [The Claude GitHub App's broad permissions (known limit)](adr/0018-claude-app-broad-permissions.md) |
