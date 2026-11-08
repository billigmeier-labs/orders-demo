# AWS environment for the orders demo. Wired in from the root justfile as
# `mod env 'infra/infra.just'`, so recipes run as `just env <recipe>`.

tf := source_directory() / "terraform"

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

# Create or update everything: base (cert, ECR, IAM, AppConfig), then env.
up:
    #!/usr/bin/env bash
    set -euo pipefail
    {{ _load_token }}
    terraform -chdir={{ tf }}/base init -input=false
    terraform -chdir={{ tf }}/base apply -input=false -auto-approve
    just --justfile {{ source_file() }} --working-directory {{ source_directory() }} secrets
    terraform -chdir={{ tf }}/env init -input=false
    terraform -chdir={{ tf }}/env apply -input=false -auto-approve

# Write the console's secrets (one JSON secret, created empty by base) from 1Password.
secrets:
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ -z "${OP_SERVICE_ACCOUNT_TOKEN:-}" ]]; then
      OP_SERVICE_ACCOUNT_TOKEN="$(cat ~/.config/op/service-account-token)"
      export OP_SERVICE_ACCOUNT_TOKEN
    fi
    arn="$(terraform -chdir={{ tf }}/base output -raw console_secret_arn)"
    jq -n \
      --arg gh "$(op read "op://Infra/orders-demo-automation/credential")" \
      --arg id "$(op read "op://Infra/google-console-oauth/username")" \
      --arg secret "$(op read "op://Infra/google-console-oauth/credential")" \
      --arg verify "$(tr -d '[:space:]' < ~/.config/op/orders-demo-console-token)" \
      '{GITHUB_TOKEN: $gh, CONSOLE_VERIFICATION_TOKEN: $verify, GOOGLE_CLIENT_ID: $id, GOOGLE_CLIENT_SECRET: $secret}' \
      | aws secretsmanager put-secret-value --region us-east-1 --secret-id "$arn" \
          --secret-string file:///dev/stdin --query VersionId --output text >/dev/null
    echo "Console secrets written to $arn"

# Destroy the per-demo environment (ALB, ECS, RDS, ...); keeps base.
down:
    #!/usr/bin/env bash
    set -euo pipefail
    # Destroy does not use the token's value; don't require 1Password for it.
    export TF_VAR_github_dispatch_token="${TF_VAR_github_dispatch_token:-unused-for-destroy}"
    terraform -chdir={{ tf }}/env init -input=false
    terraform -chdir={{ tf }}/env destroy -input=false -auto-approve

# Destroy everything: env, then base (ECR images, roles, cert, AppConfig).
nuke: down
    terraform -chdir={{ tf }}/base init -input=false
    terraform -chdir={{ tf }}/base destroy -input=false -auto-approve

# Show what `up` would change in base and env.
plan:
    #!/usr/bin/env bash
    set -euo pipefail
    {{ _load_token }}
    terraform -chdir={{ tf }}/base init -input=false
    terraform -chdir={{ tf }}/base plan -input=false
    terraform -chdir={{ tf }}/env init -input=false
    terraform -chdir={{ tf }}/env plan -input=false

# List AWS resources tagged project=orders-demo.
status:
    aws resourcegroupstaggingapi get-resources --region us-east-1 \
      --tag-filters Key=project,Values=orders-demo \
      --query 'ResourceTagMappingList[].ResourceARN' --output text \
      | tr '\t' '\n' | sort
