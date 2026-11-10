data "aws_iam_policy_document" "ecs_tasks_trust" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
    condition {
      test     = "StringEquals"
      variable = "aws:SourceAccount"
      values   = [local.account_id]
    }
  }
}

# Role names must start with the base layer's ecs_role_prefix: the GitHub
# deploy role may pass orders-demo-ecs-*, the preview role only
# orders-demo-ecs-preview-execution and orders-demo-ecs-preview-task (names in
# the base layer's locals.tf). Any same-repo PR can use the preview role, so
# those two roles must never reach anything production or console.

# ------------------------------------------------------------ execution ----

resource "aws_iam_role" "execution" {
  name               = "${local.role_pfx}execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

data "aws_iam_policy_document" "execution_secrets" {
  statement {
    actions = ["secretsmanager:GetSecretValue"]
    resources = concat(
      [for db in aws_db_instance.this : db.master_user_secret[0].secret_arn],
      [aws_secretsmanager_secret.console.arn],
    )
  }
}

resource "aws_iam_role_policy" "execution_secrets" {
  name   = "db-secrets"
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.execution_secrets.json
}

# ---------------------------------------------------- preview execution ----

# Execution role for PR previews (preview.yml registers orders-demo-pr-<N> with it). Pulls only
# orders-api-preview, writes only the preview log group, reads only the nonprod database secret.
# The ADOT sidecar image is on public ECR, which needs no permissions.
resource "aws_iam_role" "preview_execution" {
  name               = "${local.role_pfx}preview-execution"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json
}

data "aws_iam_policy_document" "preview_execution" {
  statement {
    sid       = "EcrLogin"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    sid       = "PullPreviewImages"
    actions   = ["ecr:BatchCheckLayerAvailability", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer"]
    resources = ["arn:aws:ecr:${var.region}:${local.account_id}:repository/orders-api-preview"]
  }

  statement {
    sid       = "PreviewLogs"
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["${aws_cloudwatch_log_group.this["preview"].arn}:*"]
  }

  statement {
    sid       = "NonprodDbSecret"
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [aws_db_instance.this["nonprod"].master_user_secret[0].secret_arn]
  }
}

resource "aws_iam_role_policy" "preview_execution" {
  name   = "preview-execution"
  role   = aws_iam_role.preview_execution.id
  policy = data.aws_iam_policy_document.preview_execution.json
}

# ----------------------------------------------------------- task roles ----

locals {
  appconfig_env_arns = local.appconfig.environment_arns
  # Each list is the AppConfig environments the role may read (plus X-Ray write for all).
  # preview-task is for PR previews only (passable by the GitHub preview role).
  task_roles = {
    task-prod    = ["prod"]
    task-nonprod = ["staging"]
    preview-task = ["preview"]
    console      = []
  }
}

resource "aws_iam_role" "task" {
  for_each           = local.task_roles
  name               = "${local.role_pfx}${each.key}"
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json
}

resource "aws_iam_role_policy_attachment" "task_xray" {
  for_each   = aws_iam_role.task
  role       = each.value.name
  policy_arn = "arn:aws:iam::aws:policy/AWSXRayDaemonWriteAccess"
}

data "aws_iam_policy_document" "task_appconfig" {
  for_each = { for k, v in local.task_roles : k => v if length(v) > 0 }

  statement {
    actions = ["appconfig:StartConfigurationSession", "appconfig:GetLatestConfiguration"]
    resources = [
      for env in each.value :
      "${local.appconfig_env_arns[env]}/configuration/${local.appconfig.profile_id}"
    ]
  }
}

resource "aws_iam_role_policy" "task_appconfig" {
  for_each = data.aws_iam_policy_document.task_appconfig
  name     = "appconfig"
  role     = aws_iam_role.task[each.key].id
  policy   = each.value.json
}

# The ADOT sidecar in prod and staging API tasks writes EMF records (per-endpoint metrics) to the metrics
# log group; the group exists, so it never needs CreateLogGroup. Not preview-task: previews run unreviewed
# code, and EMF records name their own namespace and dimensions, so a preview could forge prod metrics
# (preview.yml runs their collector with the bundled traces-only config).
data "aws_iam_policy_document" "task_emf" {
  statement {
    sid       = "PutEmf"
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents", "logs:DescribeLogStreams"]
    resources = [aws_cloudwatch_log_group.this["metrics"].arn, "${aws_cloudwatch_log_group.this["metrics"].arn}:*"]
  }
}

resource "aws_iam_role_policy" "task_emf" {
  for_each = toset(["task-prod", "task-nonprod"])
  name     = "emf-metrics"
  role     = aws_iam_role.task[each.key].id
  policy   = data.aws_iam_policy_document.task_emf.json
}

# The console shows what is happening in AWS (alarms, logs, deployments, cost)
# and drives the demo: starts/stops the load generator and toggles flags.
# Permissions follow src/Orders.Console/README.md ("In ECS").
locals {
  appconfig_app_arn = local.appconfig.application_arn
}

data "aws_iam_policy_document" "console" {
  statement {
    sid = "Observe"
    actions = [
      "cloudwatch:DescribeAlarmHistory",
      "cloudwatch:DescribeAlarms",
      "cloudwatch:GetMetricData",
      "cloudwatch:ListMetrics",
      "ecs:DescribeServiceDeployments",
      "ecs:DescribeServices",
      "ecs:DescribeTaskDefinition",
      "ecs:DescribeTasks",
      "ecs:ListServiceDeployments",
      "ecs:ListServices",
      "ecs:ListTasks",
      "logs:FilterLogEvents",
      "logs:GetLogEvents",
      "ce:GetCostAndUsage",
      "ce:GetTags",
    ]
    resources = ["*"]
  }

  # The current demo run and where its snapshot goes (contract: /orders-demo/demo-run,
  # /orders-demo/metrics-bucket).
  statement {
    sid     = "ReadRunParameters"
    actions = ["ssm:GetParameter", "ssm:GetParameters"]
    resources = [
      "arn:aws:ssm:${var.region}:${local.account_id}:parameter/${local.name}/demo-run",
      "arn:aws:ssm:${var.region}:${local.account_id}:parameter/${local.name}/metrics-bucket",
    ]
  }

  # Per-run snapshots in the persistent metrics bucket: runs/<N>.json, read back to compare runs.
  statement {
    sid       = "RunSnapshots"
    actions   = ["s3:GetObject", "s3:PutObject"]
    resources = ["arn:aws:s3:::${local.metrics_bucket}/runs/*"]
  }

  statement {
    sid       = "ListRunSnapshots"
    actions   = ["s3:ListBucket"]
    resources = ["arn:aws:s3:::${local.metrics_bucket}"]
    condition {
      test     = "StringLike"
      variable = "s3:prefix"
      values   = ["runs/", "runs/*"]
    }
  }

  statement {
    sid       = "RunLoadGenerator"
    actions   = ["ecs:RunTask"]
    resources = ["arn:aws:ecs:${var.region}:${local.account_id}:task-definition/${aws_ecs_task_definition.loadgen.family}:*"]
    condition {
      test     = "ArnEquals"
      variable = "ecs:cluster"
      values   = [aws_ecs_cluster.this.arn]
    }
  }

  # RunTask with tags or propagateTags needs TagResource on the new task; only at RunTask time.
  statement {
    sid       = "TagLoadGeneratorTasks"
    actions   = ["ecs:TagResource"]
    resources = ["arn:aws:ecs:${var.region}:${local.account_id}:task/${aws_ecs_cluster.this.name}/*"]
    condition {
      test     = "StringEquals"
      variable = "ecs:CreateAction"
      values   = ["RunTask"]
    }
  }

  # StopTask has no task-definition condition key, so the console tags the load generator tasks it
  # starts (component=loadgen, AwsLoadGenerator.cs) and may stop only those, never a service task.
  statement {
    sid       = "StopLoadGeneratorTasks"
    actions   = ["ecs:StopTask"]
    resources = ["arn:aws:ecs:${var.region}:${local.account_id}:task/${aws_ecs_cluster.this.name}/*"]
    condition {
      test     = "StringEquals"
      variable = "aws:ResourceTag/component"
      values   = ["loadgen"]
    }
  }

  statement {
    sid       = "PassLoadGeneratorRoles"
    actions   = ["iam:PassRole"]
    resources = [aws_iam_role.execution.arn, aws_iam_role.task["task-nonprod"].arn]
    condition {
      test     = "StringEquals"
      variable = "iam:PassedToService"
      values   = ["ecs-tasks.amazonaws.com"]
    }
  }

  statement {
    sid       = "AppConfigList"
    actions   = ["appconfig:List*"]
    resources = ["*"]
  }

  statement {
    sid = "AppConfigFlags"
    actions = [
      "appconfig:CreateHostedConfigurationVersion",
      "appconfig:GetConfigurationProfile",
      "appconfig:GetDeployment",
      "appconfig:GetHostedConfigurationVersion",
      "appconfig:GetLatestConfiguration",
      "appconfig:StartConfigurationSession",
      "appconfig:StartDeployment",
    ]
    resources = [
      local.appconfig_app_arn,
      "${local.appconfig_app_arn}/*",
      "arn:aws:appconfig:${var.region}:${local.account_id}:deploymentstrategy/*",
    ]
  }
}

resource "aws_iam_role_policy" "console_read" {
  name   = "observe"
  role   = aws_iam_role.task["console"].id
  policy = data.aws_iam_policy_document.console.json
}
