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
  for_each          = toset(["api-prod", "api-staging", "console", "migrate", "loadgen", "preview"])
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
    )
  }

  # Console settings whose names differ from its defaults (src/Orders.Console/README.md).
  # Single task, so DATAPROTECTION_KEYS_PATH stays unset: sign-ins reset on each deploy.
  console_env = concat(local.appconfig_env["prod"], [
    { name = "APPCONFIG_DEPLOYMENT_STRATEGY", value = local.base.appconfig_deployment_strategy_id },
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
    { name = key, valueFrom = "${local.base.console_secret_arn}:${key}::" }
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

  container_definitions = jsonencode([
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
  ])
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

  # Keep watching the deployment alarms for 5 minutes after the new tasks are healthy, so a version
  # that only fails under real traffic is still rolled back automatically.
  deployment_configuration {
    strategy             = "ROLLING"
    bake_time_in_minutes = 5
  }

  alarms {
    enable      = true
    rollback    = true
    alarm_names = [for a in ["5xx", "p95"] : aws_cloudwatch_metric_alarm.svc["${each.key}-${a}"].alarm_name]
  }

  tags = {
    desired_count = tostring(each.value.desired)
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
