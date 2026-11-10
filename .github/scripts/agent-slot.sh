#!/usr/bin/env bash
# Waits until this agent run may start, so at most MAX_AGENTS agent runs work at once (the spec's
# "a few agent runs at once"). GitHub's concurrency groups allow only one running job per group and
# cancel all but one waiting run, so this is a first-in-first-out queue over the Agent workflow's runs
# instead: a run starts once fewer than MAX_AGENTS older Agent runs are still unfinished. Every waiting
# run sees the same order, so the cap holds without a lock.
#
# Env: GH_TOKEN (actions: read), GITHUB_REPOSITORY, GITHUB_RUN_ID, MAX_AGENTS (3), POLL_SECONDS (30).
set -euo pipefail
max=${MAX_AGENTS:-3}
poll=${POLL_SECONDS:-30}
me=${GITHUB_RUN_ID:?}
started=$(date +%s)
while :; do
    ahead=$(gh run list -R "$GITHUB_REPOSITORY" --workflow agent.yml --limit 100 --json databaseId,status \
        --jq "[.[] | select(.status != \"completed\" and .databaseId < $me)] | length")
    if [ "$ahead" -lt "$max" ]; then
        echo "Starting: $ahead older agent run(s) still going (cap $max); waited $(( $(date +%s) - started ))s."
        exit 0
    fi
    echo "$ahead older agent runs still going (cap $max); waiting ${poll}s."
    sleep "$poll"
done
