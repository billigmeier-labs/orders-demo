# infra

AWS for the orders demo (account 983889169993, us-east-1), in two Terraform
layers with state in `s3://joshb-terraform-state-983889169993` (S3 native locking).
Every resource is tagged `project=orders-demo`.

## Layers

| Layer | State key | Holds | Lifetime |
| --- | --- | --- | --- |
| `terraform/base` | `orders-demo/base.tfstate` | ACM cert for `demo.joshb.app` + `*.demo.joshb.app` (DNS-validated); ECR repos `orders-api`, `orders-console`, `orders-loadgen` (immutable tags), `orders-api-preview`; GitHub OIDC roles `orders-demo-deploy`, `orders-demo-release`, `orders-demo-preview`, `orders-demo-incident-readonly`; AppConfig app `orders-demo` (envs `prod`, `staging`, `preview`, profile `feature-flags`) | Kept between demos |
| `terraform/env` | `orders-demo/env.tfstate` | ECS cluster `orders-demo` and services `api-prod`, `api-staging`, `console`; one-off task definitions `orders-demo-migrate-prod`, `orders-demo-migrate-staging`, `orders-demo-loadgen`; ALB + Route 53 records; RDS `orders-prod`, `orders-nonprod`; alarms; EventBridge alarm → GitHub `repository_dispatch`; SSM parameters under `/orders-demo/` | Torn down after each demo |

`env` reads `base` outputs through `terraform_remote_state`, so base must be applied first.
The GitHub OIDC provider is not managed here; base looks it up by URL and needs it to exist.

## Commands

From the repo root (the module is wired in as `just env ...`):

| Command | Does |
| --- | --- |
| `just env up` | init + apply base, then env |
| `just env plan` | plan base and env |
| `just env down` | destroy env only |
| `just env nuke` | destroy env, then base |
| `just env status` | list resources tagged `project=orders-demo` |

`up` and `plan` read the alarm-dispatch GitHub token with
`op read "op://Infra/orders-demo-automation/credential"` into `TF_VAR_github_dispatch_token`
(using `OP_SERVICE_ACCOUNT_TOKEN`, or `~/.config/op/service-account-token` if unset).
Optional: `TF_VAR_api_image_tag`, `TF_VAR_console_image_tag` (see below).

## How deploys work

- Terraform creates the services and the first task definition revision. With
  `api_image_tag` / `console_image_tag` empty (default) the services start at
  desired count 0, so `up` works before any image is pushed.
- Services ignore `task_definition` and `desired_count`. CI deploys by registering a
  new revision of the family (`orders-demo-<service>`, swapping the image and
  `APP_VERSION`/`APP_COMMIT`) and calling `UpdateService` with that revision and
  `--desired-count` from `/orders-demo/services/<service>/desired-count` (also the
  service's `desired_count` tag). Re-running `up` never reverts a deploy.
- Deployments roll back on the circuit breaker or when the service's
  `orders-demo-<service>-5xx` / `-p95` alarm fires during the deployment.
- Migrations: `RunTask` of `orders-demo-migrate-<prod|staging>` with the new image
  (container `migrate`, entry point `./efbundle`). Subnets and security group come from
  `/orders-demo/subnets` and `/orders-demo/task-security-group`; use `assignPublicIp=ENABLED`.
- Previews (`pr-<N>`): the preview role may create services, target groups (named `pr-*`)
  and HTTPS listener rules (tagged `preview=pr-<N>`; priorities above 30) for host
  `pr-<N>.demo.joshb.app`, which the wildcard record and certificate already cover.
  Inputs are under `/orders-demo/` in SSM (listener ARN, roles, log group, nonprod DB).

## Database credentials

RDS manages the master password in Secrets Manager. Tasks get
`ConnectionStrings__Orders` without a password plus `PGPASSWORD` injected from the secret;
Npgsql reads `PGPASSWORD` automatically. RDS rotates the secret (7 days by default);
tasks pick up the new value when they restart.
