#!/usr/bin/env bash
# Opens or updates one `flaky` issue per flaky test found by `just test-repeat` (the nightly Flaky tests workflow).
# Issues aren't closed automatically: a flaky test can pass many runs in a row. The PR that fixes it closes it.
#
#   flaky-issues.sh <flaky.tsv> [--dry-run]     (flaky.tsv: test, passes, failures, first failure message)
#
# Env: GH_TOKEN, GITHUB_REPOSITORY, RUN_URL.
set -euo pipefail
tsv=${1:?usage: flaky-issues.sh <flaky.tsv> [--dry-run]}
dry=false; [ "${2:-}" = --dry-run ] && dry=true
repo=${GITHUB_REPOSITORY:-local/dry-run}
run_url=${RUN_URL:-${GITHUB_SERVER_URL:-https://github.com}/$repo/actions/runs/${GITHUB_RUN_ID:-}}
summary=${GITHUB_STEP_SUMMARY:-/dev/null}
today=$(date -u +%Y-%m-%d)
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
gh_() { # gh, or in --dry-run just show the call (and any body file)
    if ! $dry; then gh "$@"; return; fi
    echo "[dry-run] gh $*" >&2
    local prev=""; for a in "$@"; do [ "$prev" = --body-file ] && sed 's/^/    | /' "$a" >&2; prev=$a; done
}

if $dry; then echo '[]' > "$work/open.json"; else
    gh label create flaky --color FBCA04 --description "A test that passes and fails on the same commit" -R "$repo" 2>/dev/null || true
    gh issue list -R "$repo" --label flaky --state open --limit 200 --json number,body > "$work/open.json"
fi

{ echo "### Flaky tests"; echo; echo "| Test | Failed | Issue |"; echo "| --- | --- | --- |"; } >> "$summary"
[ -s "$tsv" ] || { echo "| none | | |" >> "$summary"; echo "No flaky tests."; exit 0; }

while IFS=$'\t' read -r test passes failures message; do
    runs=$((passes + failures))
    method=${test##*.}; class=${test%.*}; class=${class##*.}
    file=$(grep -rlE "(void|Task|ValueTask)[[:space:]]+${method}[[:space:]]*\(" tests --include='*.cs' 2>/dev/null | head -1 || true)
    existing=$(jq -r --arg t "$test" '.[] | select(.body | contains("<!-- flaky-test: \($t) -->")) | .number' "$work/open.json" | head -1)
    cat > "$work/body.md" <<EOT
<!-- flaky-test: $test -->
### Goal

\`$test\` passes and fails on the same commit: it failed **$failures of $runs** runs in the nightly flaky-test hunt
on $today ($run_url). Make it deterministic.

Last failure:

\`\`\`
${message:-"(no message)"}
\`\`\`

**Within a day**, if it can't be fixed right away, quarantine it so it stops blocking PRs: add
\`[Trait("Category", "Quarantine")]\` to the test${file:+ in \`$file\`} in a PR that links this issue. Quarantined tests
are left out of CI but still run every night. Never add retries to make it pass.

### Acceptance criteria

- [ ] The cause is found and fixed (shared state, timing, ordering, real concurrency bug in the code...), and the PR says which
- [ ] The test has no \`Quarantine\` trait and passes \`just test-repeat 20\`
- [ ] No retries, sleeps-until-green or weakened assertions

### Risk tier

low

### Out of scope

Other tests, unless they share the cause.
EOT
    if [ -n "$existing" ]; then
        gh_ issue edit "$existing" -R "$repo" --body-file "$work/body.md"
        gh_ issue comment "$existing" -R "$repo" --body "Still flaky on $today: failed $failures of $runs runs ($run_url)."
        echo "| \`$class.$method\` | $failures/$runs | updated #$existing |" >> "$summary"
    else
        gh_ issue create -R "$repo" --title "Flaky test: $test" --label flaky --body-file "$work/body.md"
        echo "| \`$class.$method\` | $failures/$runs | opened |" >> "$summary"
    fi
done < "$tsv"
