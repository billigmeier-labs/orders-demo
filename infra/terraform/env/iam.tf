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
# orders-demo-ecs-execution and orders-demo-ecs-task-nonprod.

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
    actions   = ["secretsmanager:GetSecretValue"]
    resources = [for db in aws_db_instance.this : db.master_user_secret[0].secret_arn]
  }
}

resource "aws_iam_role_policy" "execution_secrets" {
  name   = "db-secrets"
  role   = aws_iam_role.execution.id
  policy = data.aws_iam_policy_document.execution_secrets.json
}

# ----------------------------------------------------------- task roles ----

locals {
  appconfig_env_arns = local.appconfig.environment_arns
  task_roles = {
    task-prod    = ["prod"]
    task-nonprod = ["staging", "preview"]
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

# The console shows what is happening in AWS: alarms, metrics, deployments.
data "aws_iam_policy_document" "console_read" {
  statement {
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
      "appconfig:GetConfigurationProfile",
      "appconfig:ListDeployments",
      "appconfig:GetDeployment",
      "appconfig:GetLatestConfiguration",
      "appconfig:StartConfigurationSession",
    ]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "console_read" {
  name   = "observe"
  role   = aws_iam_role.task["console"].id
  policy = data.aws_iam_policy_document.console_read.json
}
