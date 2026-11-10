#!/usr/bin/env bash
# Called when an agent run on an issue fails. First failure: label agent:failed-once, comment, and start one
# retry (re-adding the `agent` label). Second failure: label needs-human, remove `agent`, and comment what was
# tried (both runs, any branch the agent pushed, its last summary).
# Env: GH_TOKEN (automation app, so the label event starts the retry), GITHUB_REPOSITORY, ISSUE, RUN_URL, SUMMARY.
set -euo pipefail
repo=$GITHUB_REPOSITORY
gh label create agent:failed-once --color D93F0B --description "The agent failed this issue once; the next failure goes to a human" -R "$repo" 2>/dev/null || true
labels=$(gh issue view "$ISSUE" -R "$repo" --json labels --jq '[.labels[].name] | join(",")')
summary=$(jq -r '.summary // empty' <<<"${SUMMARY:-{\}}" 2>/dev/null || true)
branches=$(git ls-remote --heads origin "agent/$ISSUE-*" 2>/dev/null | sed -E 's#.*refs/heads/##' | paste -sd, - || true)

if [[ ",$labels," != *",agent:failed-once,"* ]]; then
    gh issue comment "$ISSUE" -R "$repo" --body "<!-- agent-failed: 1 -->
The agent's run on this issue failed: $RUN_URL
(an error, the time limit, or its cost or turn cap). Trying once more; a second failure hands it to a person."
    gh issue edit "$ISSUE" -R "$repo" --add-label agent:failed-once --remove-label agent
    gh issue edit "$ISSUE" -R "$repo" --add-label agent
    exit 0
fi

first=$(gh api "repos/$repo/issues/$ISSUE/comments" --paginate \
    --jq '[.[] | select(.body | startswith("<!-- agent-failed: 1 -->"))] | last | .body // ""' | grep -Eo 'https://[^ ]+/actions/runs/[0-9]+' | head -1 || true)
gh issue edit "$ISSUE" -R "$repo" --add-label needs-human --remove-label agent
gh issue comment "$ISSUE" -R "$repo" --body "<!-- agent-failed: 2 -->
The agent failed this issue twice, so it's **handed to a person** (\`needs-human\`).

What was tried:
- First run: ${first:-unknown}
- Second run: $RUN_URL
- Branch pushed: ${branches:-none}
- Agent's last summary: ${summary:-none (the run ended before it reported one)}

The run logs show where it stopped. To give it another go, fix what blocked it (or sharpen the issue),
remove \`agent:failed-once\` and \`needs-human\`, and add \`agent\` again."
