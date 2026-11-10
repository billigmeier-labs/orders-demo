#!/usr/bin/env bash
# Sets the PR's risk label (and `agent` for the agent's PRs) and fails loudly if it can't, so the
# required ai-review check enforces the labels. Needs GH_TOKEN with pull-requests: write, PR, GITHUB_REPOSITORY.
#
#   label-pr.sh <low|high> <author-login>
set -euo pipefail
tier=${1:?tier}; author=${2:?author}
case "$tier" in low|high) ;; *) echo "::error::Unknown risk tier '$tier'"; exit 1 ;; esac
other=low; [ "$tier" = low ] && other=high

args=(--add-label "risk:$tier")
current=$(gh pr view "$PR" -R "$GITHUB_REPOSITORY" --json labels --jq '[.labels[].name] | join(",")')
for stale in "risk:$other" risk:medium; do
    [[ ",$current," == *",$stale,"* ]] && args+=(--remove-label "$stale")
done
[ "$author" = "claude[bot]" ] && args+=(--add-label agent)

if ! gh pr edit "$PR" -R "$GITHUB_REPOSITORY" "${args[@]}"; then
    echo "::error title=Labels::Couldn't label PR #$PR with risk:$tier. The labels are required: check that the label exists (Issues > Labels) and that the workflow token can edit pull requests, then re-run this job."
    exit 1
fi
echo "Labelled PR #$PR risk:$tier$([ "$author" = "claude[bot]" ] && echo ", agent")."
