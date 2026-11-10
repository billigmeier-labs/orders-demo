# AWS environment for the orders demo. Wired in from the root justfile as
# `mod env 'infra/infra.just'`, so recipes run as `just env <recipe>`.
#
# `up` and `down` are unattended and safe to re-run: `up` finishes a half-built environment and skips
# what is already there; `down` sweeps what is left and ends with a leftover check.

tf := source_directory() / "terraform"
repo := "billigmeier-labs/orders-demo"
region := "us-east-1"

# Exports TF_VAR_github_dispatch_token from 1Password unless already set.
_load_token := '''
if [[ -z "${TF_VAR_github_dispatch_token:-}" ]]; then
  if [[ -z "${OP_SERVICE_ACCOUNT_TOKEN:-}" ]]; then
    OP_SERVICE_ACCOUNT_TOKEN="$(cat ~/.config/op/service-account-token)"
    export OP_SERVICE_ACCOUNT_TOKEN
  fi
  TF_VAR_github_dispatch_token="$(op read "op://Infra/orders-demo-automation/credential")"
  export TF_VAR_github_dispatch_token
fi
'''

# Sets env_resources (managed resources in the env state, empty when torn down), demo_run (SSM
# /orders-demo/demo-run, the run the env layer is tagged with) and TF_VAR_migrate_from_base.
_env_state := '''
export AWS_REGION=us-east-1 AWS_DEFAULT_REGION=us-east-1
terraform -chdir="$tf_dir"/env init -input=false >/dev/null
env_resources=$(terraform -chdir="$tf_dir"/env state list 2>/dev/null | grep -v '^data\.' || true)
demo_run=$(aws ssm get-parameter --name /orders-demo/demo-run --query Parameter.Value --output text 2>/dev/null || echo 0)
# One-time: the environment that was up when the certificate, AppConfig and console secret moved from
# base to env adopts them (env/import_from_base.tf).
if grep -qx 'aws_ecs_cluster.this' <<<"$env_resources" && ! grep -qx 'aws_appconfig_application.orders' <<<"$env_resources"; then
  export TF_VAR_migrate_from_base=true
  echo "Adopting the certificate, AppConfig and console secret from the base layer (one-time)."
fi
'''

# Build everything from nothing (or finish/refresh it): base, env, images for main, deploy staging then prod.
up:
    #!/usr/bin/env bash
    set -euo pipefail
    started=$(date +%s)
    {{ _load_token }}
    terraform -chdir={{ tf }}/base init -input=false >/dev/null
    terraform -chdir={{ tf }}/base apply -input=false -auto-approve
    tf_dir={{ tf }}
    {{ _env_state }}
    # A fresh environment is a new demo run; re-running `up` on a (partly) built one keeps its number.
    if [ -z "$env_resources" ]; then
      demo_run=$((demo_run + 1))
      aws ssm put-parameter --name /orders-demo/demo-run --value "$demo_run" --type String --overwrite >/dev/null
      echo "Demo run $demo_run: building a new environment."
    else
      echo "Demo run $demo_run: the environment already exists; updating it."
    fi
    export TF_VAR_demo_run=$demo_run
    terraform -chdir={{ tf }}/env apply -input=false -auto-approve
    just --justfile {{ source_file() }} --working-directory {{ source_directory() }} secrets

    sha=$(gh api "repos/{{ repo }}/commits/main" --jq .sha)
    echo "main is ${sha::7}"
    # Start a workflow on main and wait for the run it created; fails if the run fails.
    run_workflow() {
      local wf=$1 since id=""; shift
      since=$(date -u -d '-1 minute' +%Y-%m-%dT%H:%M:%SZ)
      gh workflow run "$wf" -R {{ repo }} --ref main "$@"
      for _ in $(seq 1 60); do
        id=$(gh run list -R {{ repo }} --workflow "$wf" --event workflow_dispatch --limit 10 --json databaseId,createdAt \
          --jq "[.[] | select(.createdAt >= \"$since\")] | last | .databaseId // empty")
        [ -n "$id" ] && break
        sleep 5
      done
      [ -n "$id" ] || { echo "Could not find the $wf run I started."; return 1; }
      echo "Waiting for $wf: https://github.com/{{ repo }}/actions/runs/$id"
      if ! gh run watch "$id" -R {{ repo }} --exit-status --interval 20 >/dev/null; then
        gh run view "$id" -R {{ repo }} --log-failed | tail -40 || true
        echo "$wf failed: https://github.com/{{ repo }}/actions/runs/$id"; return 1
      fi
    }

    # ECR is emptied by `down`; images.yml builds main's images with the release role (skips existing tags).
    missing=()
    for r in orders-api orders-console orders-loadgen; do
      aws ecr describe-images --repository-name "$r" --image-ids imageTag="$sha" >/dev/null 2>&1 || missing+=("$r")
    done
    if [ ${#missing[@]} -gt 0 ]; then
      echo "Building ${missing[*]} for ${sha::7}"
      run_workflow images.yml -f sha="$sha"
    else
      echo "Images for ${sha::7} are already in ECR."
    fi

    # deploy.yml migrates (and the services seed on first start, SEED_DATA=true) staging, then production,
    # and the console. Skipped when everything already runs main.
    running() {
      aws ecs describe-services --cluster orders-demo --services "$1" --query 'services[0].taskDefinition' --output text 2>/dev/null \
        | xargs -r aws ecs describe-task-definition --query 'taskDefinition.containerDefinitions[0].image' --output text --task-definition 2>/dev/null \
        | sed -n 's/.*://p'
    }
    if [ "$(running api-staging)" = "$sha" ] && [ "$(running api-prod)" = "$sha" ] && [ "$(running console)" = "$sha" ]; then
      echo "Staging, production and the console already run ${sha::7}."
    else
      run_workflow deploy.yml -f sha="$sha"
    fi

    echo
    echo "Demo run $demo_run is up ($(( ($(date +%s) - started) / 60 )) min):"
    terraform -chdir={{ tf }}/env output -json urls | jq -r '"  Production  \(.prod)\n  Staging     \(.staging)\n  Console     \(.console)\n  Dashboards  \(.dashboards.prod)\n              \(.dashboards.staging)"'

# Write the console's secrets (one JSON secret, created empty by env) from 1Password.
secrets:
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ -z "${OP_SERVICE_ACCOUNT_TOKEN:-}" ]]; then
      OP_SERVICE_ACCOUNT_TOKEN="$(cat ~/.config/op/service-account-token)"
      export OP_SERVICE_ACCOUNT_TOKEN
    fi
    arn="$(terraform -chdir={{ tf }}/env output -raw console_secret_arn)"
    jq -n \
      --arg gh "$(op read "op://Infra/orders-demo-automation/credential")" \
      --arg id "$(op read "op://Infra/google-console-oauth/username")" \
      --arg secret "$(op read "op://Infra/google-console-oauth/credential")" \
      --arg verify "$(tr -d '[:space:]' < ~/.config/op/orders-demo-console-token)" \
      '{GITHUB_TOKEN: $gh, CONSOLE_VERIFICATION_TOKEN: $verify, GOOGLE_CLIENT_ID: $id, GOOGLE_CLIENT_SECRET: $secret}' \
      | aws secretsmanager put-secret-value --region {{ region }} --secret-id "$arn" \
          --secret-string file:///dev/stdin --query VersionId --output text >/dev/null
    echo "Console secrets written to $arn"

# Wipe the demo: close demo PRs and issues, remove previews, flags, images, then destroy env; ends with a leftover check.
down:
    #!/usr/bin/env bash
    set -euo pipefail
    started=$(date +%s)
    # Destroy does not use the token's value; don't require 1Password for it.
    export TF_VAR_github_dispatch_token="${TF_VAR_github_dispatch_token:-unused-for-destroy}"
    tf_dir={{ tf }}
    {{ _env_state }}
    unset TF_VAR_migrate_from_base
    export TF_VAR_demo_run=$demo_run
    echo "Tearing down demo run $demo_run."

    # 1. GitHub: demo PRs closed (branches deleted), demo issues deleted, incident issues closed.
    just --justfile {{ justfile_directory() }}/demo.just --working-directory {{ justfile_directory() }} cleanup

    # 2. What CI and the console created outside Terraform: previews, one-off tasks.
    if aws ecs describe-clusters --clusters orders-demo --query 'clusters[?status==`ACTIVE`].clusterName' --output text | grep -q orders-demo; then
      previews=$(aws ecs list-services --cluster orders-demo --query 'serviceArns[]' --output text | tr '\t' '\n' | sed -nE 's#.*/(pr-[0-9]+)$#\1#p')
      for s in $previews; do
        aws ecs delete-service --cluster orders-demo --service "$s" --force >/dev/null 2>&1 && echo "Deleting preview service $s" || true
      done
      [ -z "$previews" ] || aws ecs wait services-inactive --cluster orders-demo --services $previews || true
      for t in $(aws ecs list-tasks --cluster orders-demo --query 'taskArns[]' --output text); do
        group=$(aws ecs describe-tasks --cluster orders-demo --tasks "$t" --query 'tasks[0].group' --output text)
        case "$group" in family:*) aws ecs stop-task --cluster orders-demo --task "$t" --reason "just env down" >/dev/null && echo "Stopped task ${t##*/} ($group)" ;; esac
      done
    fi
    if listener=$(aws ssm get-parameter --name /orders-demo/https-listener-arn --query Parameter.Value --output text 2>/dev/null); then
      for rule in $(aws elbv2 describe-rules --listener-arn "$listener" --query 'Rules[?IsDefault==`false` && to_number(Priority) >= `1000`].RuleArn' --output text 2>/dev/null); do
        aws elbv2 delete-rule --rule-arn "$rule" && echo "Deleted preview listener rule"
      done
    fi
    for tg in $(aws elbv2 describe-target-groups --query 'TargetGroups[?starts_with(TargetGroupName, `pr-`)].TargetGroupArn' --output text); do
      aws elbv2 delete-target-group --target-group-arn "$tg" && echo "Deleted target group ${tg##*:targetgroup/}"
    done

    # 3. AppConfig: the account's deletion protection blocks deleting recently used environments and
    # profiles, so delete the whole application here (BYPASS) and drop it from the state.
    app=$(aws appconfig list-applications --query "Items[?Name=='orders-demo'].Id" --output text)
    if [ -n "$app" ] && [ "$app" != None ]; then
      for prof in $(aws appconfig list-configuration-profiles --application-id "$app" --query 'Items[].Id' --output text); do
        for v in $(aws appconfig list-hosted-configuration-versions --application-id "$app" --configuration-profile-id "$prof" --query 'Items[].VersionNumber' --output text); do
          aws appconfig delete-hosted-configuration-version --application-id "$app" --configuration-profile-id "$prof" --version-number "$v"
        done
        aws appconfig delete-configuration-profile --application-id "$app" --configuration-profile-id "$prof" --deletion-protection-check BYPASS
      done
      for e in $(aws appconfig list-environments --application-id "$app" --query 'Items[].Id' --output text); do
        aws appconfig delete-environment --application-id "$app" --environment-id "$e" --deletion-protection-check BYPASS
      done
      aws appconfig delete-application --application-id "$app"
      echo "Deleted AppConfig application orders-demo ($app)"
    fi
    for s in $(aws appconfig list-deployment-strategies --query "Items[?Name=='orders-demo-instant'].Id" --output text); do
      aws appconfig delete-deployment-strategy --deployment-strategy-id "$s" && echo "Deleted deployment strategy $s"
    done
    mapfile -t gone < <(grep '^aws_appconfig_' <<<"$env_resources" || true)
    [ ${#gone[@]} -eq 0 ] || terraform -chdir={{ tf }}/env state rm "${gone[@]}" >/dev/null

    # 4. Every image in the release and preview repositories (the repositories stay; see base/ecr.tf).
    # A few passes: manifests an image index still references are only deletable once the index is gone.
    for r in orders-api orders-console orders-loadgen orders-api-preview; do
      for _ in 1 2 3; do
        ids=$(aws ecr list-images --repository-name "$r" --query 'imageIds[*].{imageDigest:imageDigest}' --output json 2>/dev/null || echo '[]')
        n=$(jq 'map(.imageDigest) | unique | length' <<<"$ids")
        [ "$n" = 0 ] && break
        jq -c 'map({imageDigest}) | unique | . as $a | range(0; length; 100) | $a[.:.+100]' <<<"$ids" | while read -r chunk; do
          aws ecr batch-delete-image --repository-name "$r" --image-ids "$chunk" --query 'length(imageIds)' --output text \
            | xargs -I{} echo "Deleted {} images from $r"
        done
      done
    done

    # 5. Terraform: ECS, ALB, RDS (no final snapshot), DNS records, certificate, alarms, dashboards, logs, SSM.
    terraform -chdir={{ tf }}/env destroy -input=false -auto-approve

    # 6. Task definition revisions CI registered (deploys, migrations, previews): deregister, then delete.
    for td in $(aws ecs list-task-definitions --status ACTIVE --query 'taskDefinitionArns[]' --output text | tr '\t' '\n' | grep '/orders-demo-' || true); do
      aws ecs deregister-task-definition --task-definition "$td" >/dev/null
    done
    aws ecs list-task-definitions --status INACTIVE --query 'taskDefinitionArns[]' --output text | tr '\t' '\n' | grep '/orders-demo-' \
      | xargs -r -n 10 aws ecs delete-task-definitions --query 'length(taskDefinitions)' --output text --task-definitions >/dev/null || true

    # 7. The RDS-managed master password secrets go with their instances; make sure none waits out a recovery window.
    for arn in $(aws resourcegroupstaggingapi get-resources --resource-type-filters secretsmanager:secret \
        --tag-filters Key=project,Values=orders-demo --query 'ResourceTagMappingList[].ResourceARN' --output text); do
      case "$arn" in *:secret:rds\!db-*)
        aws secretsmanager delete-secret --secret-id "$arn" --force-delete-without-recovery >/dev/null 2>&1 && echo "Deleted ${arn##*:secret:}" || true ;;
      esac
    done

    echo "Torn down in $(( ($(date +%s) - started) / 60 )) min. Checking for leftovers..."
    just --justfile {{ source_file() }} --working-directory {{ source_directory() }} leftovers "$demo_run"

# Fail with a list if anything from a demo run is left in AWS (everything tagged project=orders-demo or demo-run=<run> except demo-run=persistent, plus what tagging can't see)
leftovers run="":
    #!/usr/bin/env bash
    set -euo pipefail
    export AWS_REGION={{ region }} AWS_DEFAULT_REGION={{ region }}
    run="{{ run }}"
    # Real API checks decide pass/fail. The tagging index keeps deleted resources for hours, so it's only reported.
    find_leftovers() {
      for r in orders-api orders-console orders-loadgen orders-api-preview; do
        n=$(aws ecr list-images --repository-name "$r" --query 'length(imageIds)' --output text 2>/dev/null || echo 0)
        [ "$n" = 0 ] || echo "ECR $r: $n images"
      done
      for s in ACTIVE INACTIVE; do
        aws ecs list-task-definitions --status "$s" --query 'taskDefinitionArns[]' --output text | tr '\t' '\n' | grep '/orders-demo-' | sed "s/^/$s task definition /" || true
      done
      aws ecs describe-clusters --clusters orders-demo --query "clusters[?status=='ACTIVE'].clusterArn" --output text | grep -v '^$' | sed 's/^/ECS cluster /' || true
      aws rds describe-db-instances --query "DBInstances[?starts_with(DBInstanceIdentifier, 'orders-demo')].DBInstanceIdentifier" --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/RDS instance /' || true
      aws elbv2 describe-load-balancers --query "LoadBalancers[?starts_with(LoadBalancerName, 'orders-demo')].LoadBalancerName" --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/Load balancer /' || true
      aws elbv2 describe-target-groups --query "TargetGroups[?starts_with(TargetGroupName, 'orders-demo')].TargetGroupName" --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/Target group /' || true
      aws ec2 describe-security-groups --filters Name=tag:project,Values=orders-demo --query 'SecurityGroups[].GroupName' --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/Security group /' || true
      aws secretsmanager list-secrets --query "SecretList[?starts_with(Name, 'orders-demo') || starts_with(Name, 'rds!db')].Name" --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/Secret /' || true
      aws events list-rules --name-prefix orders-demo --query 'Rules[].Name' --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/EventBridge rule /' || true
      aws appconfig list-applications --query "Items[?Name=='orders-demo'].Id" --output text | grep -v '^$' | sed 's/^/AppConfig application /' || true
      aws cloudwatch describe-alarms --alarm-name-prefix orders-demo- --query 'MetricAlarms[].AlarmName' --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/Alarm /' || true
      aws cloudwatch list-dashboards --dashboard-name-prefix orders-demo- --query 'DashboardEntries[].DashboardName' --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/Dashboard /' || true
      for p in /ecs/orders-demo /aws/events/orders-demo; do
        aws logs describe-log-groups --log-group-name-prefix "$p" --query 'logGroups[].logGroupName' --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/Log group /' || true
      done
      zone=$(terraform -chdir={{ tf }}/base output -raw zone_id 2>/dev/null || echo Z04795242Y2F7953D3FBP)
      aws route53 list-resource-record-sets --hosted-zone-id "$zone" --output json \
        | jq -r '.ResourceRecordSets[] | select(.Name | test("(^|\\.)demo\\.joshb\\.app\\.$")) | "DNS record \(.Name) \(.Type)"'
      aws acm list-certificates --query 'CertificateSummaryList[?DomainName==`demo.joshb.app`].CertificateArn' --output text | tr '\t' '\n' | grep -v '^$' | sed 's/^/Certificate /' || true
    }
    tagged() {
      {
        aws resourcegroupstaggingapi get-resources --tag-filters Key=project,Values=orders-demo --output json
        [ -z "$run" ] || aws resourcegroupstaggingapi get-resources --tag-filters Key=demo-run,Values="$run" --output json
      } | jq -rs '[.[].ResourceTagMappingList[]] | unique_by(.ResourceARN)
          | map(select(([.Tags[] | select(.Key == "demo-run") | .Value][0] // "") != "persistent")) | length'
    }
    for attempt in $(seq 1 20); do
      left=$(find_leftovers | sort -u)
      if [ -z "$left" ]; then
        echo "Nothing left behind${run:+ by demo run $run} (kept: tagged demo-run=persistent, the Route 53 zone, the state bucket, the OIDC provider)."
        n=$(tagged); [ "$n" = 0 ] || echo "(AWS's tagging index still lists $n deleted resources; it catches up within a few hours.)"
        exit 0
      fi
      [ "$attempt" = 20 ] && break
      echo "$(wc -l <<<"$left") leftovers, waiting for deletions to settle (attempt $attempt/20)..."
      sleep 30
    done
    echo "Left behind:"; sed 's/^/  /' <<<"$left"
    exit 1

# Destroy everything: env, then base (roles, ECR repositories, the demo-run counter; the metrics bucket only if empty).
nuke: down
    terraform -chdir={{ tf }}/base init -input=false
    terraform -chdir={{ tf }}/base destroy -input=false -auto-approve

# Show what `up` would change in base and env (read-only).
plan:
    #!/usr/bin/env bash
    set -euo pipefail
    {{ _load_token }}
    terraform -chdir={{ tf }}/base init -input=false >/dev/null
    terraform -chdir={{ tf }}/base plan -input=false -lock=false
    tf_dir={{ tf }}
    {{ _env_state }}
    [ -n "$env_resources" ] || demo_run=$((demo_run + 1))
    export TF_VAR_demo_run=$demo_run
    terraform -chdir={{ tf }}/env plan -input=false -lock=false

# List AWS resources tagged project=orders-demo, with their demo-run tag.
status:
    aws resourcegroupstaggingapi get-resources --region {{ region }} \
      --tag-filters Key=project,Values=orders-demo --output json \
      | jq -r '.ResourceTagMappingList[] | "\([.Tags[] | select(.Key == "demo-run") | .Value][0] // "-")\t\(.ResourceARN)"' \
      | sort
