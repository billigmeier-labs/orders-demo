# Demo resets: put the repo back the way the scenarios expect. `just demo reset` does everything.
# Run from a clean checkout of main. Changes to main go through the admin bypass (logged by GitHub).

set shell := ["bash", "-euo", "pipefail", "-c"]

repo := "billigmeier-labs/orders-demo"
search_v2 := "src/Orders.Api/Features/Search/SearchV2.cs"

# Everything below, in order
reset: flags-off bad-deploy incident cleanup
    @echo "Demo reset complete."

# Turn every AppConfig flag off in prod, staging and preview (via the console's API would be nicer; this is direct)
flags-off:
    #!/usr/bin/env bash
    set -euo pipefail
    app=$(aws appconfig list-applications --query "Items[?Name=='orders-demo'].Id" --output text)
    prof=$(aws appconfig list-configuration-profiles --application-id "$app" --query "Items[?Name=='feature-flags'].Id" --output text)
    strategy=$(aws appconfig list-deployment-strategies --query "Items[?Name=='orders-demo-instant'].Id" --output text)
    tmp=$(mktemp)
    cat >"$tmp" <<'JSON'
    {"version":"1","flags":{"searchV2":{"name":"searchV2"},"faultSlowSearch":{"name":"faultSlowSearch","attributes":{"percent":{"constraints":{"type":"number","minimum":0,"maximum":100}}}},"faultFailOrders":{"name":"faultFailOrders","attributes":{"percent":{"constraints":{"type":"number","minimum":0,"maximum":100}}}}},"values":{"searchV2":{"enabled":false},"faultSlowSearch":{"enabled":false,"percent":20},"faultFailOrders":{"enabled":false,"percent":20}}}
    JSON
    version=$(aws appconfig create-hosted-configuration-version --application-id "$app" --configuration-profile-id "$prof" \
        --content-type application/json --content "fileb://$tmp" /dev/null --query VersionNumber --output text)
    for env in $(aws appconfig list-environments --application-id "$app" --query 'Items[].Id' --output text); do
        aws appconfig start-deployment --application-id "$app" --environment-id "$env" --configuration-profile-id "$prof" \
            --configuration-version "$version" --deployment-strategy-id "$strategy" --description "demo reset: flags off" >/dev/null
    done
    rm -f "$tmp"
    echo "All flags off (version $version)."

# Remove the canned bug from main if a demo merged it, and refresh the demo/canned-bug branch on top of main
bad-deploy:
    #!/usr/bin/env bash
    set -euo pipefail
    git fetch -q origin main demo/canned-bug
    git checkout -q main && git reset -q --hard origin/main
    bug_title="perf(search): run the product and order lookups in parallel"
    merged=$(git log --format='%H %s' origin/main | grep -F "$bug_title" | head -1 | cut -d' ' -f1 || true)
    if [ -n "$merged" ] && ! git log --format=%s origin/main | grep -qF "Revert \"$bug_title"; then
        git revert --no-edit "$merged"
        git push -q origin main
        echo "Reverted the canned bug on main ($merged)."
    fi
    # Rebuild the branch as main + the bug commit, so the demo PR shows just the bug.
    bug=$(git log --format=%H -1 origin/demo/canned-bug)
    git checkout -q -B demo/canned-bug origin/main
    git cherry-pick -q "$bug" || { git cherry-pick --abort; echo "Cherry-pick conflicted; fix demo/canned-bug by hand."; exit 1; }
    git push -q -f origin demo/canned-bug
    git checkout -q main
    echo "demo/canned-bug is main + the bug."

# Put the SearchV2 multi-word bug back if an agent fixed it: revert the agent's fix commit (code and its test together)
incident:
    #!/usr/bin/env bash
    set -euo pipefail
    git checkout -q main && git fetch -q origin main && git reset -q --hard origin/main
    if grep -q 'TsQuerySyntax().Replace(term, string.Empty)}:\*' {{ search_v2 }}; then
        echo "SearchV2 bug already present."; exit 0
    fi
    fix=$(git log --format='%H %an' -- {{ search_v2 }} | awk '$2 ~ /^claude/ {print $1; exit}')
    if [ -z "$fix" ]; then echo "SearchV2 changed but not by the agent; fix by hand."; exit 1; fi
    git revert --no-edit "$fix"
    git push -q origin main
    echo "Reverted the agent's SearchV2 fix ($fix); the bug is back behind the searchV2 flag."

# Close demo PRs (deleting their branches) and delete demo issues
cleanup:
    #!/usr/bin/env bash
    set -euo pipefail
    gh pr list -R {{ repo }} --label demo --state open --json number,headRefName --jq '.[] | "\(.number) \(.headRefName)"' |
    while read -r pr branch; do
        # The canned-bug branch is reused by every bad-deploy demo; keep it.
        if [ "$branch" = demo/canned-bug ]; then flag=""; else flag="--delete-branch"; fi
        gh pr close "$pr" -R {{ repo }} $flag -c "Closed by demo reset." >/dev/null && echo "Closed PR #$pr"
    done
    for id in $(gh issue list -R {{ repo }} --label demo --state all --limit 200 --json id --jq '.[].id'); do
        gh api graphql -f query='mutation($id:ID!){deleteIssue(input:{issueId:$id}){clientMutationId}}' -f id="$id" >/dev/null && echo "Deleted issue $id"
    done
