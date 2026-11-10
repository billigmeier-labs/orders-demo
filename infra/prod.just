# Production pause switch (spec: "A GitHub environment switch can pause it during an incident").
# Wired in from the root justfile as `mod prod 'infra/prod.just'`: `just prod pause`, `just prod resume`,
# `just prod status`. Pausing adds you as a required reviewer on the `production` GitHub environment, so
# every production deploy job (the API and the console) waits in Actions until someone approves it or
# the pause is lifted; staging keeps deploying. Needs `gh` signed in as a repo admin.

set shell := ["bash", "-euo", "pipefail", "-c"]

repo := "billigmeier-labs/orders-demo"

# Body for PUT /environments/production that keeps everything except the reviewers
_env_body := '''
current=$(gh api "repos/$REPO/environments/production")
body() {
  jq --argjson reviewers "$1" '{
      wait_timer: ([.protection_rules[]? | select(.type == "wait_timer") | .wait_timer][0] // 0),
      prevent_self_review: false,
      reviewers: $reviewers,
      deployment_branch_policy: .deployment_branch_policy
    }' <<<"$current"
}
'''

# Hold production deploys: each one waits for approval in Actions (or `just prod resume`)
pause:
    #!/usr/bin/env bash
    set -euo pipefail
    REPO={{ repo }}
    {{ _env_body }}
    me=$(gh api user --jq .id)
    body "[{\"type\": \"User\", \"id\": $me}]" | gh api -X PUT "repos/$REPO/environments/production" --input - >/dev/null
    echo "Production is paused: deploys wait for a review (you are the reviewer). Staging keeps deploying."
    echo "Approve one deploy in the run's page in Actions, or lift the pause with: just prod resume"

# Lift the pause and approve the production deploys that are waiting
resume:
    #!/usr/bin/env bash
    set -euo pipefail
    REPO={{ repo }}
    {{ _env_body }}
    body '[]' | gh api -X PUT "repos/$REPO/environments/production" --input - >/dev/null
    echo "Production is no longer paused."
    # Jobs that were already waiting keep waiting for a review after the rule is gone; approve them.
    for run in $(gh api "repos/$REPO/actions/runs?status=waiting&per_page=100" --jq '.workflow_runs[].id'); do
      ids=$(gh api "repos/$REPO/actions/runs/$run/pending_deployments" --jq '[.[] | select(.environment.name == "production" and .current_user_can_approve) | .environment.id]')
      [ "$ids" = "[]" ] && continue
      jq -n --argjson ids "$ids" '{environment_ids: $ids, state: "approved", comment: "Production pause lifted (just prod resume)"}' \
        | gh api -X POST "repos/$REPO/actions/runs/$run/pending_deployments" --input - >/dev/null
      echo "Approved the waiting production deploy in https://github.com/$REPO/actions/runs/$run"
    done

# Is production paused, and which deploys are waiting?
status:
    #!/usr/bin/env bash
    set -euo pipefail
    REPO={{ repo }}
    gh api "repos/$REPO/environments/production" --jq '
      ([.protection_rules[]? | select(.type == "required_reviewers") | .reviewers[].reviewer.login] | join(", ")) as $r
      | if $r == "" then "Production deploys automatically (not paused)." else "Production is paused: deploys wait for \($r)." end'
    gh api "repos/$REPO/actions/runs?status=waiting&per_page=100" \
      --jq '.workflow_runs[] | "Waiting: \(.name) \(.head_sha[0:7]) \(.html_url)"'
