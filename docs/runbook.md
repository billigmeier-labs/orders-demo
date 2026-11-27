# Running the demo

For whoever presents or maintains the demo. Commands are in [infra/README.md](../infra/README.md) (AWS) and
`demo.just` (scenarios); this page is the order to run them in and what to check first.

## Before a demo

1. **Anthropic credit.** The Claude Code action (agents, triage, AI review) bills the `ANTHROPIC_API_KEY`
   organization's *purchased* credit. A promotional credit there covers the API and Agent SDK but not Claude Code,
   so CI fails with "Credit balance is too low" even while the promo shows a balance. Top up before a demo.
2. **Credentials** (1Password vault `Service - Agent Orders Demo`; `just env up` and `just env secrets` read it with
   the token in `~/.op-agent-token`, see [infra/README.md](../infra/README.md)):
   - `anthropic-orders-demo`: the repo secret `ANTHROPIC_API_KEY` (also a Dependabot secret).
   - `orders-automation-app`: the `orders-automation` GitHub App (App ID 5681201), as repo and Dependabot
     secrets `AUTOMATION_APP_ID` / `AUTOMATION_APP_KEY`. The PEM is stored on one line; rebuild the newlines when
     piping it into `gh secret set`.
   - `orders-demo-automation`: the fine-grained PAT (expires 2027-11-09), used only for the AWS alarm →
     `repository_dispatch` and by the console. `just env up` writes it into AWS.
   - `google-console-oauth`: the console's Google sign-in client.
3. **`gh`** signed in as a repo admin, with the `project` scope for the Projects board
   (`gh auth refresh -s project`).
4. **Build the environment:** `just env up` (about 20 minutes, unattended), then `just demo reset`.
5. **Sign in** at https://console.demo.joshb.app with Google (billigmeier.org). Check that the Status tab is all
   green and every alarm is OK before starting a scenario.

## During

- The console's Present tab runs the Tour and each scenario. `just demo reset` (flags off, revert the agents'
  last changes, rebuild the demo branches, close demo PRs and incidents) puts everything back between runs.
- Console run state is in memory: restarting or redeploying the console loses a run in progress. Merges that
  don't touch the console don't redeploy it.
- The admin bypass on `main` and direct admin pushes are intentional ([ADR 0001](adr/0001-greenfield-commits-to-main.md)).
- Projects board: https://github.com/orgs/billigmeier-labs/projects/1.

## After

- `just env down`, then confirm it ends with "Nothing left behind". AWS's tagging index keeps listing deleted
  resources for a few hours; the leftover check reports that count but doesn't fail on it.
- Cost per run: Cost Explorer, grouped by the `demo-run` cost allocation tag.
- Each run sends a lot of GitHub notification email (CI, AI review, triage, previews); it's all addressed to
  `orders-demo@noreply.github.com`, so one mail filter can skip the inbox for it.

## Known limits

- Previews log in to the non-prod database as its master user ([ADR 0017](adr/0017-previews-share-the-nonprod-login.md)).
- The Claude GitHub App has broad permissions ([ADR 0018](adr/0018-claude-app-broad-permissions.md)).
- NBomber (the load generator) is licensed for personal use only. Fine for this demo; reusing it at work needs a
  licence or a different tool.

## Things that bit us

- gitleaks 8.24 reads `[allowlist]` in `.gitleaks.toml`, not the newer `[[allowlists]]`. CI scans only `HEAD`.
- SSM `GetParameters` takes at most 10 names per call.
- jq 1.7 removed `_nwise`; chunk arrays with `range` instead.
