# Load scenarios. `just load start <scenario> --minutes <N>` runs the NBomber load generator as an ECS task
# against production, exactly as the console's Load control does (same task definition, overrides and tags,
# startedBy orders-console, so the console shows it and can stop it). `just load local` runs it here.
# Scenarios (src/Orders.LoadGenerator): normal ≈ 5 req/s, peak ≈ 25, spike = normal with a 60 s burst to 60,
# soak = normal for a long time.

set shell := ["bash", "-euo", "pipefail", "-c"]

dotnet := "./scripts/dotnet"
configuration := "Release"
api_port := "8080"
region := "us-east-1"

# Start a scenario on ECS (stops a running one first), e.g. `just load start spike --minutes 10`
start scenario="normal" *args:
    #!/usr/bin/env bash
    set -euo pipefail
    export AWS_REGION={{ region }} AWS_DEFAULT_REGION={{ region }}
    scenario="{{ scenario }}"
    case "$scenario" in normal|peak|spike|soak) ;; *) echo "Unknown scenario '$scenario' (normal, peak, spike, soak)"; exit 1 ;; esac
    minutes=""
    set -- {{ args }}
    while [ $# -gt 0 ]; do
      case "$1" in
        --minutes) minutes="${2:-}"; shift 2 || shift ;;
        --minutes=*) minutes="${1#--minutes=}"; shift ;;
        *) echo "Unknown option $1 (use --minutes <N>)"; exit 1 ;;
      esac
    done
    [ -n "$minutes" ] || { [ "$scenario" = soak ] && minutes=30 || minutes=5; }
    [[ "$minutes" =~ ^[0-9]+$ ]] && [ "$minutes" -ge 1 ] && [ "$minutes" -le 240 ] || { echo "--minutes must be 1-240"; exit 1; }

    params=$(aws ssm get-parameters --names /orders-demo/cluster /orders-demo/loadgen/family /orders-demo/subnets \
      /orders-demo/task-security-group /orders-demo/demo-run --query 'Parameters[].[Name,Value]' --output json)
    get() { jq -r --arg n "/orders-demo/$1" '.[] | select(.[0] == $n) | .[1]' <<<"$params"; }
    cluster=$(get cluster)
    [ -n "$cluster" ] || { echo "The environment is down (no /orders-demo/cluster); run just env up first."; exit 1; }

    just --justfile {{ source_file() }} stop >/dev/null
    overrides=$(jq -cn --arg s "$scenario" --arg m "$minutes" \
      '{containerOverrides: [{name: "loadgen", environment: [{name: "SCENARIO", value: $s}, {name: "DURATION_MINUTES", value: $m}]}]}')
    run=$(aws ecs run-task --cluster "$cluster" --task-definition "$(get loadgen/family)" --launch-type FARGATE --count 1 \
      --started-by orders-console \
      --tags key=project,value=orders-demo key=component,value=loadgen key=demo-run,value="$(get demo-run)" \
      --network-configuration "awsvpcConfiguration={subnets=[$(get subnets)],securityGroups=[$(get task-security-group)],assignPublicIp=ENABLED}" \
      --overrides "$overrides")
    task=$(jq -r '.tasks[0].taskArn // empty' <<<"$run")
    [ -n "$task" ] || { echo "RunTask failed: $(jq -c .failures <<<"$run")"; exit 1; }
    echo "Started $scenario for $minutes min (task ${task##*/}); it ends around $(date -d "+$minutes min" '+%-I:%M %p'). Stop it with: just load stop"

# Stop the running load generator task(s), whether the console or `just load start` started them
stop:
    #!/usr/bin/env bash
    set -euo pipefail
    export AWS_REGION={{ region }} AWS_DEFAULT_REGION={{ region }}
    cluster=$(aws ssm get-parameter --name /orders-demo/cluster --query Parameter.Value --output text 2>/dev/null) || { echo "The environment is down."; exit 0; }
    tasks=$(aws ecs list-tasks --cluster "$cluster" --started-by orders-console --desired-status RUNNING --query 'taskArns[]' --output text)
    [ -n "$tasks" ] || { echo "No load running."; exit 0; }
    for t in $tasks; do
      aws ecs stop-task --cluster "$cluster" --task "$t" --reason "just load stop" >/dev/null
      echo "Stopped ${t##*/}"
    done

# Show the running load generator task, its scenario and when it ends
status:
    #!/usr/bin/env bash
    set -euo pipefail
    export AWS_REGION={{ region }} AWS_DEFAULT_REGION={{ region }}
    cluster=$(aws ssm get-parameter --name /orders-demo/cluster --query Parameter.Value --output text 2>/dev/null) || { echo "The environment is down."; exit 0; }
    tasks=$(aws ecs list-tasks --cluster "$cluster" --started-by orders-console --desired-status RUNNING --query 'taskArns[]' --output text)
    [ -n "$tasks" ] || { echo "No load running."; exit 0; }
    # shellcheck disable=SC2086 # task ARNs, split on purpose
    aws ecs describe-tasks --cluster "$cluster" --tasks $tasks --output json | jq -r '.tasks[] |
      (.overrides.containerOverrides[0].environment // [] | map({(.name): .value}) | add // {}) as $e
      | "\(.taskArn | split("/") | last)  \($e.SCENARIO // "?") for \($e.DURATION_MINUTES // "?") min  \(.lastStatus)  started \(.startedAt // .createdAt)"'

# Run the load generator here against TARGET_BASE_URL (default the `just dev` API), e.g. `just load local spike 5`
local scenario="normal" minutes="1":
    #!/usr/bin/env bash
    set -euo pipefail
    export SCENARIO={{ scenario }} DURATION_MINUTES={{ minutes }}
    export TARGET_BASE_URL="${TARGET_BASE_URL:-http://localhost:{{ api_port }}}"
    if ! command -v dotnet >/dev/null 2>&1; then
        # In the SDK container, localhost is the container: reach the host's port instead.
        TARGET_BASE_URL=$(sed -E 's#^(https?://)(localhost|127\.0\.0\.1)([:/]|$)#\1host.docker.internal\3#' <<<"$TARGET_BASE_URL")
        export DOTNET_DOCKER_ARGS="-e TARGET_BASE_URL -e SCENARIO -e DURATION_MINUTES"
    fi
    {{ dotnet }} run --project src/Orders.LoadGenerator -c {{ configuration }}
