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
    if [ -n "$(git status --porcelain)" ]; then echo "Commit your changes first; demo resets need a clean checkout."; exit 1; fi
    git checkout -q main
    git fetch -q origin main
    git merge -q --ff-only origin/main
    # The original bug commit: the first commit whose subject starts with the bug's title (never a Revert/Reapply).
    bug=$(git log --reverse --format='%H%x09%s' origin/main | awk -F'\t' 'index($2, "perf(search): run the product and order lookups in parallel") == 1 && !f {print $1; f=1}')
    if [ -z "$bug" ]; then bug=$(git rev-parse -q --verify origin/demo/canned-bug 2>/dev/null || true); fi
    if [ -z "$bug" ]; then echo "Can't find the canned bug commit."; exit 1; fi
    # Is the bug on main right now? Decide from the code, not from commit titles.
    if grep -q 'new NpgsqlConnection' src/Orders.Api/Features/Search/SearchEndpoints.cs; then
        git diff "$bug^" "$bug" | git apply -R
        git commit -qam "revert(demo): remove the canned bug after a bad-deploy demo"
        git push -q origin main
        echo "Removed the canned bug from main."
    fi
    # Rebuild the branch as main + the bug, so the demo PR shows just the bug.
    git checkout -q -B demo/canned-bug origin/main
    git diff "$bug^" "$bug" | git apply
    git commit -qam "perf(search): run the product and order lookups in parallel"
    git push -q -f origin demo/canned-bug
    git checkout -q main
    echo "demo/canned-bug is main + the bug."

# Put the SearchV2 multi-word bug back if an agent fixed it: revert the agent's fix commit (code and its test together)
incident:
    #!/usr/bin/env bash
    set -euo pipefail
    if [ -n "$(git status --porcelain)" ]; then echo "Commit your changes first; demo resets need a clean checkout."; exit 1; fi
    git checkout -q main && git fetch -q origin main && git merge -q --ff-only origin/main
    if grep -q 'TsQuerySyntax().Replace(term, string.Empty)}:\*' {{ search_v2 }}; then
        echo "SearchV2 bug already present."; exit 0
    fi
    fix=$(git log --format='%H %an' -- {{ search_v2 }} | awk '$2 ~ /^claude/ && !f {print $1; f=1}')
    if [ -z "$fix" ]; then echo "SearchV2 changed but not by the agent; fix by hand."; exit 1; fi
    git revert --no-edit "$fix"
    git push -q origin main
    echo "Reverted the agent's SearchV2 fix ($fix); the bug is back behind the searchV2 flag."

# Close demo PRs (deleting their branches) and delete demo issues
cleanup:
    #!/usr/bin/env bash
    set -euo pipefail
    # Demo PRs: labelled `demo`, or opened from the canned-bug branch.
    { gh pr list -R {{ repo }} --label demo --state open --json number,headRefName --jq '.[] | "\(.number) \(.headRefName)"'
      gh pr list -R {{ repo }} --head demo/canned-bug --state open --json number,headRefName --jq '.[] | "\(.number) \(.headRefName)"'; } | sort -u |
    while read -r pr branch; do
        # The canned-bug branch is reused by every bad-deploy demo; keep it.
        if [ "$branch" = demo/canned-bug ]; then flag=""; else flag="--delete-branch"; fi
        gh pr close "$pr" -R {{ repo }} $flag -c "Closed by demo reset." >/dev/null && echo "Closed PR #$pr"
    done
    for id in $(gh issue list -R {{ repo }} --label demo --state all --limit 200 --json id --jq '.[].id'); do
        gh api graphql -f query='mutation($id:ID!){deleteIssue(input:{issueId:$id}){clientMutationId}}' -f id="$id" >/dev/null && echo "Deleted issue $id"
    done

# Turn one flag on or off in one environment, e.g. `just demo flag searchV2 on prod`
flag name state env="prod":
    #!/usr/bin/env bash
    set -euo pipefail
    app=$(aws appconfig list-applications --query "Items[?Name=='orders-demo'].Id" --output text)
    prof=$(aws appconfig list-configuration-profiles --application-id "$app" --query "Items[?Name=='feature-flags'].Id" --output text)
    envid=$(aws appconfig list-environments --application-id "$app" --query "Items[?Name=='{{ env }}'].Id" --output text)
    strategy=$(aws appconfig list-deployment-strategies --query "Items[?Name=='orders-demo-instant'].Id" --output text)
    latest=$(aws appconfig list-hosted-configuration-versions --application-id "$app" --configuration-profile-id "$prof" --query 'max_by(Items,&VersionNumber).VersionNumber' --output text)
    cur=$(mktemp); new=$(mktemp)
    aws appconfig get-hosted-configuration-version --application-id "$app" --configuration-profile-id "$prof" --version-number "$latest" "$cur" >/dev/null
    enabled=$([ "{{ state }}" = on ] && echo true || echo false)
    jq --arg f "{{ name }}" --argjson e "$enabled" 'del(.. | ._createdAt?, ._updatedAt?) | .values[$f].enabled = $e' "$cur" > "$new"
    version=$(aws appconfig create-hosted-configuration-version --application-id "$app" --configuration-profile-id "$prof" \
        --content-type application/json --content "fileb://$new" /dev/null --query VersionNumber --output text)
    aws appconfig start-deployment --application-id "$app" --environment-id "$envid" --configuration-profile-id "$prof" \
        --configuration-version "$version" --deployment-strategy-id "$strategy" --description "{{ name }} {{ state }}" >/dev/null
    rm -f "$cur" "$new"
    echo "{{ name }} is {{ state }} in {{ env }} (version $version)."
