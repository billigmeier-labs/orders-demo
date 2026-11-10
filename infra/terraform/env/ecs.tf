resource "aws_ecs_cluster" "this" {
  name = local.name

  setting {
    name  = "containerInsights"
    value = "disabled"
  }
}

resource "aws_ecs_cluster_capacity_providers" "this" {
  cluster_name       = aws_ecs_cluster.this.name
  capacity_providers = ["FARGATE"]
}

resource "aws_cloudwatch_log_group" "this" {
  # metrics: the collector sidecars' EMF records (CloudWatch turns them into OrdersDemo metrics).
  for_each          = toset(["api-prod", "api-staging", "console", "migrate", "loadgen", "preview", "metrics"])
  name              = "/ecs/${local.name}/${each.key}"
  retention_in_days = 7
}

locals {
  # Before the first image exists the services are created with this tag and
  # desired_count 0, so nothing tries to pull it.
  bootstrap_tag = "bootstrap"

  appconfig_env = {
    for env in ["prod", "staging", "preview"] : env => [
      { name = "APPCONFIG_APPLICATION", value = "orders-demo" },
      { name = "APPCONFIG_ENVIRONMENT", value = env },
      { name = "APPCONFIG_PROFILE", value = "feature-flags" },
    ]
  }

  common_env = [
    { name = "AWS_REGION", value = var.region },
    { name = "ASPNETCORE_HTTP_PORTS", value = "8080" },
  ]

  log_config = {
    for k in keys(aws_cloudwatch_log_group.this) : k => {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.this[k].name
        awslogs-region        = var.region
        awslogs-stream-prefix = k
      }
    }
  }

  # API tasks run an ADOT collector sidecar that takes OTLP on localhost:4317 (gRPC) and 4318 (HTTP):
  # traces go to X-Ray (task role: AWSXRayDaemonWriteAccess), and the API's per-endpoint request metric
  # goes to CloudWatch as EMF in the metrics log group (task role: PutLogEvents there, iam.tf), which
  # CloudWatch turns into namespace OrdersDemo, metric RequestDuration (seconds; SampleCount = requests,
  # p95 = latency), dimension sets in otel_metric_dimensions. The alarms stay on ALB metrics.
  # ADOT v0.51 has no transform processor, so status_class comes from the app (docs/contracts.md).
  otel_collector_image = "public.ecr.aws/aws-observability/aws-otel-collector:v0.51.0"
  otel_services        = toset([for k, s in local.services : k if s.repo == "orders-api"])
  otel_metric_dimensions = [
    ["service", "environment", "endpoint", "status_class"],
    ["service", "environment", "endpoint"],
    ["service", "environment", "status_class"],
    ["service", "environment"],
  ]
  otel_config = {
    for k in local.otel_services : k => yamlencode({
      receivers = {
        otlp = { protocols = { grpc = { endpoint = "0.0.0.0:4317" }, http = { endpoint = "0.0.0.0:4318" } } }
      }
      processors = {
        "batch/traces"  = { timeout = "1s", send_batch_size = 50 }
        "batch/metrics" = { timeout = "30s" }
        # Only the request metric; the SDK's runtime and client metrics would each cost a CloudWatch metric.
        "filter/requests" = { metrics = { include = { match_type = "strict", metric_names = ["http.server.request.duration"] } } }
        # Short dimension names from the resource attributes the API sets (OTEL_SERVICE_NAME,
        # deployment.environment.name = APP_ENVIRONMENT) and the route template.
        "resource/dimensions" = { attributes = [
          { key = "service", from_attribute = "service.name", action = "insert" },
          { key = "environment", from_attribute = "deployment.environment.name", action = "insert" },
        ] }
        "metricstransform/requests" = { transforms = [{
          include    = "http.server.request.duration"
          action     = "update"
          new_name   = "RequestDuration"
          operations = [{ action = "update_label", label = "http.route", new_label = "endpoint" }]
        }] }
        # The API is asked to export delta temporality; this converts if it sends cumulative anyway.
        cumulativetodelta = {}
      }
      exporters = {
        awsxray = {}
        awsemf = {
          namespace                        = "OrdersDemo"
          log_group_name                   = aws_cloudwatch_log_group.this["metrics"].name
          log_stream_name                  = k
          resource_to_telemetry_conversion = { enabled = true }
          dimension_rollup_option          = "NoDimensionRollup"
          metric_declarations = [{
            dimensions            = local.otel_metric_dimensions
            metric_name_selectors = ["^RequestDuration$"]
          }]
        }
      }
      service = {
        pipelines = {
          traces = { receivers = ["otlp"], processors = ["batch/traces"], exporters = ["awsxray"] }
          metrics = {
            receivers  = ["otlp"]
            processors = ["filter/requests", "resource/dimensions", "metricstransform/requests", "cumulativetodelta", "batch/metrics"]
            exporters  = ["awsemf"]
          }
        }
      }
    })
  }

  service_tag = { for k, s in local.services : k => (s.tag != "" ? s.tag : local.bootstrap_tag) }

  service_env = {
    for k, s in local.services : k => concat(
      local.common_env,
      [
        { name = "APP_ENVIRONMENT", value = s.env },
        { name = "APP_VERSION", value = local.service_tag[k] },
        { name = "APP_COMMIT", value = local.service_tag[k] },
      ],
      s.db == null ? [] : concat(
        [{ name = "ConnectionStrings__Orders", value = local.connection_strings[s.db] }],
        local.appconfig_env[s.env],
        # A demo: every environment seeds an empty database, prod included.
        [{ name = "SEED_DATA", value = "true" }],
      ),
      k == "console" ? local.console_env : [],
      contains(local.otel_services, k) ? [
        { name = "OTEL_EXPORTER_OTLP_ENDPOINT", value = "http://localhost:4317" },
        { name = "OTEL_SERVICE_NAME", value = "orders-${k}" },
        # CloudWatch wants per-interval values; one export a minute matches its resolution.
        { name = "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE", value = "delta" },
        { name = "OTEL_METRIC_EXPORT_INTERVAL", value = "60000" },
      ] : [],
    )
  }

  # Console settings whose names differ from its defaults (src/Orders.Console/README.md).
  # Single task, so DATAPROTECTION_KEYS_PATH stays unset: sign-ins reset on each deploy.
  console_env = concat(local.appconfig_env["prod"], [
    { name = "APPCONFIG_DEPLOYMENT_STRATEGY", value = aws_appconfig_deployment_strategy.instant.id },
    { name = "ECS_CLUSTER", value = local.name },
    { name = "ECS_PROD_SERVICE", value = "api-prod" },
    { name = "ECS_STAGING_SERVICE", value = "api-staging" },
    { name = "ALARM_PREFIX", value = "${local.name}-" },
    { name = "PROD_LOG_GROUP", value = aws_cloudwatch_log_group.this["api-prod"].name },
    { name = "GITHUB_REPOSITORY", value = var.github_repo },
    { name = "PROD_URL", value = "https://${local.domain}" },
    { name = "STAGING_URL", value = "https://staging.${local.domain}" },
    { name = "LOADGEN_TASK_DEFINITION", value = aws_ecs_task_definition.loadgen.family },
    { name = "LOADGEN_CONTAINER", value = "loadgen" },
    { name = "LOADGEN_SUBNETS", value = join(",", local.subnet_ids) },
    { name = "LOADGEN_SECURITY_GROUPS", value = aws_security_group.tasks.id },
    # Default-VPC public subnets without NAT: the task needs a public IP to pull its image.
    { name = "LOADGEN_ASSIGN_PUBLIC_IP", value = "true" },
  ])

  # One Secrets Manager secret (base layer), one JSON key per variable.
  console_secrets = [
    for key in ["GITHUB_TOKEN", "CONSOLE_VERIFICATION_TOKEN", "GOOGLE_CLIENT_ID", "GOOGLE_CLIENT_SECRET"] :
    { name = key, valueFrom = "${aws_secretsmanager_secret.console.arn}:${key}::" }
  ]
}

resource "aws_ecs_task_definition" "svc" {
  for_each = local.services

  family                   = "${local.name}-${each.key}"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "256"
  memory                   = "512"
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task[each.value.task_role].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "ARM64"
  }

  container_definitions = jsonencode(concat([
    {
      name         = each.key
      image        = "${local.ecr[each.value.repo]}:${local.service_tag[each.key]}"
      essential    = true
      portMappings = [{ containerPort = 8080, protocol = "tcp" }]
      environment  = local.service_env[each.key]
      secrets = each.key == "console" ? local.console_secrets : each.value.db == null ? [] : [
        { name = "PGPASSWORD", valueFrom = local.db_password_secret[each.value.db] }
      ]
      logConfiguration = local.log_config[each.key]
    }
    ], contains(local.otel_services, each.key) ? [
    {
      # Non-essential: if the collector dies the API keeps serving, it just stops exporting.
      name              = "otel-collector"
      image             = local.otel_collector_image
      essential         = false
      cpu               = 32
      memory            = 128
      memoryReservation = 64
      # AOT_CONFIG_CONTENT replaces the bundled config (traces to X-Ray, metrics to CloudWatch EMF).
      environment = [
        { name = "AWS_REGION", value = var.region },
        { name = "AOT_CONFIG_CONTENT", value = local.otel_config[each.key] },
      ]
      logConfiguration = local.log_config[each.key]
    }
  ] : []))
}

resource "aws_ecs_service" "svc" {
  for_each = local.services

  name            = each.key
  cluster         = aws_ecs_cluster.this.id
  task_definition = aws_ecs_task_definition.svc[each.key].arn
  desired_count   = each.value.tag != "" ? each.value.desired : 0
  launch_type     = "FARGATE"

  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200
  health_check_grace_period_seconds  = 60
  propagate_tags                     = "SERVICE"
  wait_for_steady_state              = false

  network_configuration {
    subnets          = local.subnet_ids
    security_groups  = [aws_security_group.tasks.id]
    assign_public_ip = true
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.svc[each.key].arn
    container_name   = each.key
    container_port   = 8080
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  # Keep watching the deployment alarms for bake_minutes after the new tasks are healthy, so a version
  # that only fails under real traffic is still rolled back automatically.
  deployment_configuration {
    strategy             = "ROLLING"
    bake_time_in_minutes = each.value.bake_minutes
  }

  alarms {
    enable      = true
    rollback    = true
    alarm_names = local.deployment_alarms[each.key]
  }

  # bake_minutes: `just rollback` restores it after a no-bake manual rollback.
  tags = {
    desired_count = tostring(each.value.desired)
    bake_minutes  = tostring(each.value.bake_minutes)
  }

  # CI owns what runs: it registers new task definition revisions and calls
  # UpdateService with --desired-count from the service's desired_count tag
  # (or SSM). Terraform only sets both on create, so re-applies never revert
  # a deploy or scale a running service back to 0.
  lifecycle {
    ignore_changes = [task_definition, desired_count]
  }

  depends_on = [aws_lb_listener_rule.svc]
}

# ------------------------------------------------------- one-off tasks ----

resource "aws_ecs_task_definition" "migrate" {
  for_each = { prod = "prod", staging = "nonprod" }

  family                   = "${local.name}-migrate-${each.key}"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "256"
  memory                   = "512"
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task[each.key == "prod" ? "task-prod" : "task-nonprod"].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "ARM64"
  }

  container_definitions = jsonencode([
    {
      name       = "migrate"
      image      = "${local.ecr["orders-api"]}:${var.api_image_tag != "" ? var.api_image_tag : local.bootstrap_tag}"
      essential  = true
      entryPoint = var.migrate_command
      environment = concat(local.common_env, [
        { name = "APP_ENVIRONMENT", value = each.key },
        { name = "ConnectionStrings__Orders", value = local.connection_strings[each.value] },
      ])
      secrets          = [{ name = "PGPASSWORD", valueFrom = local.db_password_secret[each.value] }]
      logConfiguration = local.log_config["migrate"]
    }
  ])
}

resource "aws_ecs_task_definition" "loadgen" {
  family                   = "${local.name}-loadgen"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "512"
  memory                   = "1024"
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task["task-nonprod"].arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "ARM64"
  }

  container_definitions = jsonencode([
    {
      name      = "loadgen"
      image     = "${local.ecr["orders-loadgen"]}:${var.loadgen_image_tag}"
      essential = true
      environment = concat(local.common_env, [
        # The demo story is customers on production, so the load goes there.
        { name = "TARGET_BASE_URL", value = "https://${local.domain}" },
      ])
      logConfiguration = local.log_config["loadgen"]
    }
  ])
}

# Drops PR databases on the non-prod instance: preview-down.yml runs it with PR=<N> when a PR closes, the
# reaper with KEEP=<open PR numbers> to drop every other pr_<N>. Named orders-demo-pr-* so the GitHub
# preview role may run it; it uses the preview execution role and security group, which reach only the
# non-prod database (the same access a PR's own preview already has).
locals {
  dropdb_script = <<-SH
    set -eu
    export PGHOST="$DB_HOST" PGUSER="$DB_USER" PGDATABASE=orders PGSSLMODE=require PGCONNECT_TIMEOUT=15
    if [ -n "$${PR:-}" ]; then
      case "$PR" in ''|*[!0-9]*) echo "PR must be a number"; exit 2;; esac
      targets="pr_$PR"
    else
      case "$${KEEP:-}" in *[!0-9\ ]*) echo "KEEP must be PR numbers separated by spaces"; exit 2;; esac
      targets=$(psql -v ON_ERROR_STOP=1 -At -c "select datname from pg_database where datname ~ '^pr_[0-9]+$'")
      for n in $${KEEP:-}; do targets=$(printf '%s\n' $targets | grep -vx "pr_$n" || true); done
    fi
    for db in $targets; do
      psql -v ON_ERROR_STOP=1 -q -c "DROP DATABASE IF EXISTS \"$db\" WITH (FORCE)"
      echo "Dropped database $db (if it existed)"
    done
    echo "Done."
  SH
}

resource "aws_ecs_task_definition" "preview_dropdb" {
  family                   = "${local.name}-pr-dropdb"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "256"
  memory                   = "512"
  execution_role_arn       = aws_iam_role.preview_execution.arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "ARM64"
  }

  container_definitions = jsonencode([
    {
      name      = "dropdb"
      image     = "public.ecr.aws/docker/library/postgres:17-alpine"
      essential = true
      command   = ["sh", "-c", local.dropdb_script]
      environment = [
        { name = "DB_HOST", value = aws_db_instance.this["nonprod"].address },
        { name = "DB_USER", value = aws_db_instance.this["nonprod"].username },
      ]
      secrets = [{ name = "PGPASSWORD", valueFrom = local.db_password_secret["nonprod"] }]
      logConfiguration = {
        logDriver = "awslogs"
        options = {
          awslogs-group         = aws_cloudwatch_log_group.this["preview"].name
          awslogs-region        = var.region
          awslogs-stream-prefix = "dropdb"
        }
      }
    }
  ])
}
