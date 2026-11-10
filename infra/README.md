# infra

AWS for the orders demo (account 983889169993, us-east-1), in two Terraform
layers with state in `s3://joshb-terraform-state-983889169993` (S3 native locking).
Every resource is tagged `project=orders-demo` and `demo-run`: the run number for what one demo builds,
`persistent` for what stays between demos.

## Layers

| Layer | State key | Holds | Lifetime |
| --- | --- | --- | --- |
| `terraform/base` | `orders-demo/base.tfstate` | GitHub OIDC roles `orders-demo-deploy`, `orders-demo-release`, `orders-demo-preview`, `orders-demo-incident-readonly`; ECR repositories `orders-api`, `orders-console`, `orders-loadgen` (immutable tags), `orders-api-preview` (empty between demos); SSM `/orders-demo/demo-run`; metrics bucket `orders-demo-metrics-983889169993` and SSM `/orders-demo/metrics-bucket` | Kept between demos (`demo-run=persistent`) |
| `terraform/env` | `orders-demo/env.tfstate` | ACM certificate for `demo.joshb.app` + `*.demo.joshb.app` and its validation record; ALB and the host records; ECS cluster `orders-demo`, services `api-prod`, `api-staging`, `console`, one-off task definitions (migrations, load generator, preview database drop); RDS `orders-prod`, `orders-nonprod`; AppConfig app `orders-demo` (envs `prod`, `staging`, `preview`, profile `feature-flags`); console secret `orders-demo/console`; alarms, dashboards, log groups; EventBridge alarm → GitHub `repository_dispatch`; SSM parameters under `/orders-demo/` | One demo run (`demo-run=<N>`): `just env down` wipes it |

Kept outside this repo: the Route 53 zone `joshb.app`, the state bucket and GitHub's OIDC provider (infra repo).
`env` reads `base` outputs through `terraform_remote_state`, so base must be applied first.

## Commands

From the repo root:

| Command | Does |
| --- | --- |
| `just env up` | Builds or finishes the environment, unattended: apply base, start demo run N+1 if env is empty, apply env, write the console secrets, build main's images (`images.yml`) if ECR lacks them, run `deploy.yml` for main (migrate, then staging, then prod, and the console; first starts seed both databases), print the URLs. Re-running skips what is done. |
| `just env down` | `just demo cleanup` (close demo PRs and delete their branches, delete demo issues, close incidents), remove previews and one-off tasks, delete the AppConfig application, delete every ECR image, `terraform destroy` env, delete the task definition revisions CI registered, then `just env leftovers`. |
| `just env leftovers [run]` | Fails with a list if anything tagged `project=orders-demo` or `demo-run=<run>` (except `demo-run=persistent`) is left, or images, task definitions, AppConfig, alarms, dashboards, log groups, DNS records under `demo.joshb.app` or its certificate. Retries for up to 10 minutes while deletions settle. |
| `just env plan` | Read-only plan of base and env (no lock). |
| `just env nuke` | `down`, then destroy base too (fails on the metrics bucket while it holds snapshots, on purpose). |
| `just env status` | Resources tagged `project=orders-demo`, with their `demo-run`. |
| `just load start <scenario> --minutes <N>` / `just load stop` / `just load status` | Load generator on ECS against production, same as the console's control (`normal`, `peak`, `spike`, `soak`). `just load local <scenario> <minutes>` runs it here. |
| `just prod pause` / `just prod resume` / `just prod status` | Hold production deploys during an incident (below). |
| `just rollback [prod\|staging]` | Previous release, no bake, in about two minutes (below). |

`up` and `plan` read the alarm-dispatch GitHub token with
`op read "op://Infra/orders-demo-automation/credential"` into `TF_VAR_github_dispatch_token`
(using `OP_SERVICE_ACCOUNT_TOKEN`, or `~/.config/op/service-account-token` if unset). `up` and `down` also
need `gh` signed in with rights to run workflows and close PRs and issues. `TF_VAR_demo_run` comes from SSM.

## How deploys work

- Terraform creates the services and the first task definition revision (image `:bootstrap`, desired
  count 0), so `up` works before any image is pushed.
- Services ignore `task_definition` and `desired_count`. CI deploys by registering a new revision of the
  family (`orders-demo-<service>`, copying the newest revision with its tags and swapping the image and
  `APP_VERSION`/`APP_COMMIT`) and calling `UpdateService` with that revision and `--desired-count` from
  `/orders-demo/services/<service>/desired-count`. Re-running `up` never reverts a deploy; changes to the
  task definition in Terraform reach the services with the next deploy.
- Deployments roll back on the circuit breaker or when `orders-demo-<service>-5xx`, `-p95` or (API)
  `-error-rate` fires during the rollout or the bake (staging 1 minute, prod 4, console 3).
- Migrations: `RunTask` of `orders-demo-migrate-<prod|staging>` with the new image (container `migrate`,
  entry point `./efbundle`, `--propagate-tags TASK_DEFINITION`). deploy.yml skips it when
  `src/Orders.Api/Data/Migrations` is unchanged since the commit the environment runs.
- Previews (`pr-<N>`): the preview role may create services, target groups (named `pr-*`) and HTTPS listener
  rules (tagged `preview=pr-<N>`; priorities from 1000) for host `pr-<N>.demo.joshb.app`, which the wildcard
  record and certificate cover. Closing the PR drops database `pr_<N>` too (`orders-demo-pr-dropdb`, a
  postgres client task with the preview execution role and security group); the nightly reaper drops any
  `pr_<N>` whose PR is not open.

### Merge to production (target: under 15 minutes)

Before: about 17 to 19 minutes (Prepare 1.5, staging 5, production 9 with a 5-minute bake). Changes:

| Change | Saves |
| --- | --- |
| Migration task only when migrations changed (each is a Fargate start plus image pull) | about 1 min per environment on most merges |
| ALB health check every 5 s instead of 10 (healthy after 2), deregistration delay 10 s (was 15; the 300 s default was already gone) | about 15 s per rollout |
| Prod bake 4 minutes instead of 5 (the 5xx alarm needs 2 one-minute datapoints plus about a minute of ALB metric delay, about 3 minutes after the first errors, and ECS watches from the first new task, so the bad-deploy demo still rolls back) | 1 min |
| deploy-service.yml polls every 5 s instead of 10; smoke test retries every 2 s | 10 to 20 s per job |
| The console deploy already runs beside staging and prod. Staging and prod stay in sequence (production follows a green staging). | |

Estimate: Prepare about 1 min, staging about 3 (rollout 1.5, bake 1, smoke), production about 6
(rollout 1.5, bake 4, smoke), plus job start-up: **about 10 to 11 minutes**, or 12 to 13 when the merge
carries a migration.

### Rollback by hand (target: under 5 minutes)

`just rollback` (or `just rollback staging`) finds the newest release (task definition revision with a
commit-SHA image) and the newest older revision with a different image, and deploys that with bake time 0
and the deployment alarms off for this rollout, so the alarm still in ALARM from the bad version cannot
roll the rollback back. When the rollout completes it restores the service's bake (its `bake_minutes`
tag) and alarms with an `UpdateService` that does not start a deployment, and prints the elapsed time
(about 1.5 to 2 minutes for prod). Re-running it, or running it after ECS already rolled back, is a no-op.
The next merge to main deploys again; `just prod pause` first if that should wait.

### Production pause

`just prod pause` adds you as a required reviewer on the `production` GitHub environment (keeping its
branch policy), so every production deploy job, the API's and the console's, waits in Actions until it
is approved there. Staging keeps deploying. `just prod resume` removes the reviewer and approves the
deploys that were waiting; `just prod status` shows the state. Needs a repo admin's `gh`.

## Observability

- Logs: `/ecs/orders-demo/<service>` (7 days). Traces: X-Ray, through the ADOT sidecar.
- Per-endpoint metrics: the ADOT sidecar's inline config (`AOT_CONFIG_CONTENT`, `env/ecs.tf`) turns the
  API's `http.server.request.duration` into CloudWatch namespace `OrdersDemo`, metric `RequestDuration`,
  dimensions service, environment, endpoint, status_class (contract in `docs/contracts.md`), via EMF records
  in `/ecs/orders-demo/metrics`. Prod and staging task roles may write there; previews send traces only.
- Alarms (`env/alarms.tf`), per environment: `-5xx`, `-p95`, `-error-rate` (metric math, 5 % of at least 20
  requests a minute), `-unhealthy` (ALB `UnHealthyHostCount`), `-task-restarts` (EventBridge ECS task state
  changes → `/aws/events/orders-demo/task-stops` → metric filter `OrdersDemo TaskStops`). All are named
  `orders-demo-*`, so EventBridge sends them to `incident.yml`.
- Dashboards: `orders-demo-prod`, `orders-demo-staging`.

## Database credentials

RDS manages the master password in Secrets Manager. Tasks get
`ConnectionStrings__Orders` without a password plus `PGPASSWORD` injected from the secret;
Npgsql reads `PGPASSWORD` automatically. RDS rotates the secret (7 days by default);
tasks pick up the new value when they restart.

## Moving the certificate, AppConfig and console secret from base to env (one time)

These used to be in base. `base/moved_to_env.tf` has `removed { lifecycle { destroy = false } }` blocks
(base forgets them without deleting anything) and `env/import_from_base.tf` has import blocks for the live
IDs, enabled by `TF_VAR_migrate_from_base=true`, which `just env up` and `just env plan` set by themselves
when the env state has the cluster but not the AppConfig application. So, after this is merged to main:

1. `just env plan`: base shows the 10 resources as "will no longer be managed" and creates the bucket and
   the two SSM parameters; env shows 13 imports and otherwise tag updates, the new alarms, dashboards and
   tasks. (Before base is applied the plan shows `demo-run = "0"`; the apply uses 1.)
2. `just env up`: applies base first (the moved resources are then in no state; nothing changes in AWS),
   then env with the imports (demo run 1), then rebuilds/deploys as usual. Don't run anything else against
   base or env in between.
3. After that, `env/import_from_base.tf` can be deleted (it does nothing once the state has the resources).

If the env apply fails after base was applied, re-run `just env up`: the imports are still pending and the
condition still holds.
