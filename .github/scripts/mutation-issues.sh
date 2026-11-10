#!/usr/bin/env bash
# Turns a Stryker mutation report into work (the nightly Mutation workflow):
#   - prints the overall score as `::notice title=mutation-score::<pct>` (the console reads it) and in the summary
#   - one issue per file with at least MIN_SURVIVORS surviving mutants, written like the agent task form and
#     labelled mutation, agent, risk:low; an open issue for the file is updated in place instead of duplicated
#   - closes open mutation issues whose file now has no surviving mutants, with a comment saying why
#   - opens (or updates) one needs-human issue when the score is below the threshold, and closes it on recovery
#
#   mutation-issues.sh <mutation-report.json> [--dry-run]
#
# Env: GH_TOKEN, GITHUB_REPOSITORY, MIN_SURVIVORS (3), MAX_ISSUES (new issues per run, 2), SCORE_THRESHOLD
# (default: stryker-config.json thresholds.low), RUN_URL. --dry-run prints what it would do without calling GitHub.
set -euo pipefail
report=${1:?usage: mutation-issues.sh <mutation-report.json> [--dry-run]}
dry=false; [ "${2:-}" = --dry-run ] && dry=true
min=${MIN_SURVIVORS:-3}
max_new=${MAX_ISSUES:-2}
threshold=${SCORE_THRESHOLD:-$(jq -r '."stryker-config".thresholds.low' stryker-config.json)}
run_url=${RUN_URL:-${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-}/actions/runs/${GITHUB_RUN_ID:-}}
repo=${GITHUB_REPOSITORY:-local/dry-run}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT

gh_() { # gh, or in --dry-run just show the call (and any body file)
    if ! $dry; then gh "$@"; return; fi
    echo "[dry-run] gh $*" >&2
    local prev=""; for a in "$@"; do [ "$prev" = --body-file ] && sed 's/^/    | /' "$a" >&2; prev=$a; done
}

# --- Score ----------------------------------------------------------------------------------------
# Killed and Timeout count as caught; Survived and NoCoverage as not caught (Stryker's own definition).
read -r killed weak score < <(jq -r '
    [.files[].mutants[]] as $m
    | ($m | map(select(.status == "Killed" or .status == "Timeout")) | length) as $k
    | ($m | map(select(.status == "Survived" or .status == "NoCoverage")) | length) as $w
    | "\($k) \($w) \(if ($k + $w) > 0 then ($k * 10000 / ($k + $w) | round) / 100 else 0 end)"' "$report")
echo "::notice title=mutation-score::$score"
{
    echo "### Mutation score: $score% (threshold $threshold%)"
    echo
    echo "$killed mutants caught by tests, $weak not caught."
    echo
    echo "| File | Not caught | Issue |"
    echo "| --- | --- | --- |"
} >> "$summary"

# file<TAB>survivors for every file, worst first; paths relative to the repo (src/...).
jq -r '.files | to_entries[]
    | {f: (.key | sub("^.*?/src/"; "src/")), n: ([.value.mutants[] | select(.status == "Survived" or .status == "NoCoverage")] | length)}
    | "\(.f)\t\(.n)"' "$report" | sort -t$'\t' -k2,2nr -k1,1 > "$work/files.tsv"

# Open mutation issues, keyed by the file in their hidden marker.
if $dry; then cat "${OPEN_JSON:-/dev/null}" > "$work/open.json"; [ -s "$work/open.json" ] || echo '[]' > "$work/open.json"; else
    gh issue list -R "$repo" --label mutation --state open --limit 200 --json number,body > "$work/open.json"
fi
issue_for() { jq -r --arg f "$1" '.[] | select(.body | contains("<!-- mutation-file: \($f) -->")) | .number' "$work/open.json" | head -1; }

body_for() { # body_for <file> <count>: the agent task form, filled in
    local file=$1 count=$2 full
    full=$(jq -r --arg f "$file" '.files | keys[] | select(endswith("/" + $f) or . == $f)' "$report" | head -1)
    cat <<EOF
<!-- mutation-file: $file -->
### Goal

Make the tests catch the $count mutants that survive in \`$file\`. Nightly mutation testing changed this file's code
on purpose (each row below), and **no test failed**: each one is a behaviour the tests don't actually check.

| Line | What Stryker changed | Status |
| --- | --- | --- |
$(jq -r --arg f "$full" '.files[$f].mutants[] | select(.status == "Survived" or .status == "NoCoverage")
    | "| \(.location.start.line) | \(.mutatorName): `\(.replacement // "" | gsub("\n"; " ") | gsub("\\|"; "\\|") | .[0:80])` | \(.status) |"' "$report" | head -40)

### Acceptance criteria

- [ ] Each mutant above is killed by a new or improved unit or integration test (check with \`just mutation\`; the next nightly run closes this issue when none survive)
- [ ] No production code changes, unless a mutant shows a real bug (then say so in the PR)
- [ ] \`just ci\` passes

### Risk tier

low

### Out of scope

Changing \`$file\` itself beyond what a real bug needs; other files' mutants (they get their own issues).

_Updated by the nightly mutation run: ${run_url}_
EOF
}

opened=0
while IFS=$'\t' read -r file count; do
    existing=$(issue_for "$file")
    if [ "$count" -eq 0 ]; then
        if [ -n "$existing" ]; then
            gh_ issue close "$existing" -R "$repo" --comment "The nightly mutation run ($run_url) found no surviving mutants in \`$file\` any more, so this is done. Closing."
            echo "| \`$file\` | 0 | closed #$existing |" >> "$summary"
        fi
        continue
    fi
    if [ "$count" -lt "$min" ] && [ -z "$existing" ]; then
        echo "| \`$file\` | $count | below $min, no issue |" >> "$summary"
        continue
    fi
    body_for "$file" "$count" > "$work/body.md"
    if [ "$count" = 1 ]; then what="1 mutant survives"; else what="$count mutants survive"; fi
    title="Strengthen tests: $what in $file"
    if [ -n "$existing" ]; then
        gh_ issue edit "$existing" -R "$repo" --title "$title" --body-file "$work/body.md"
        echo "| \`$file\` | $count | updated #$existing |" >> "$summary"
    elif [ "$opened" -lt "$max_new" ]; then
        # Labelled for the agent at creation: mutation issues are pre-triaged (tests only, low risk).
        gh_ issue create -R "$repo" --title "$title" --label mutation,agent,risk:low --body-file "$work/body.md"
        opened=$((opened + 1))
        echo "| \`$file\` | $count | opened |" >> "$summary"
    else
        echo "| \`$file\` | $count | waiting (max $max_new new issues per run) |" >> "$summary"
    fi
done < "$work/files.tsv"

# Open issues whose file is gone from the report (deleted or excluded from mutation) are closed too.
jq -r '.[] | .number as $n | (.body | capture("<!-- mutation-file: (?<f>[^ ]+) -->").f // empty) | "\($n)\t\(.)"' "$work/open.json" |
while IFS=$'\t' read -r number file; do
    grep -q "^$file"$'\t' "$work/files.tsv" && continue
    gh_ issue close "$number" -R "$repo" --comment "\`$file\` is no longer mutation-tested (moved, deleted or excluded in stryker-config.json), so there are no mutants left to kill. Closing."
done

# --- Score below the threshold: one issue for the human ------------------------------------------------
title="Mutation score $score% is below the $threshold% threshold"
if $dry; then low_issue=""; else
    low_issue=$(gh issue list -R "$repo" --label mutation,needs-human --state open --search "in:title Mutation score" --json number --jq '.[0].number // empty')
fi
if awk -v a="$score" -v b="$threshold" 'BEGIN { exit !(a < b) }'; then
    cat > "$work/low.md" <<EOF
<!-- mutation-score-low -->
The nightly mutation run ($run_url) scored **$score%**, below the threshold of $threshold%
(\`thresholds.low\` in stryker-config.json). $weak mutants survive across the API.

Per-file issues for agents are opened automatically (at most $max_new new ones a night). This one is for a person:
decide whether the tests need a bigger push, whether the threshold is right, or whether code was added without tests.
It closes itself when the score is back at or above the threshold.
EOF
    if [ -n "$low_issue" ]; then
        gh_ issue edit "$low_issue" -R "$repo" --title "$title" --body-file "$work/low.md"
    else
        gh_ issue create -R "$repo" --title "$title" --label mutation,needs-human --body-file "$work/low.md"
    fi
    echo "::warning title=Mutation score below threshold::$score% < $threshold%"
elif [ -n "$low_issue" ]; then
    gh_ issue close "$low_issue" -R "$repo" --comment "The mutation score is back to $score% (threshold $threshold%) in $run_url. Closing."
fi
echo "Mutation score $score%; opened $opened new issue(s)."
