# The GitHub OIDC provider is owned by another repo (created by the
# orchestrator); look it up by URL. It must exist before this layer plans.
data "aws_iam_openid_connect_provider" "github" {
  url = "https://token.actions.githubusercontent.com"
}

locals {
  github_oidc_host         = "token.actions.githubusercontent.com"
  github_oidc_provider_arn = data.aws_iam_openid_connect_provider.github.arn
  repo_sub                 = "repo:${var.github_repo}"
}

data "aws_iam_policy_document" "trust" {
  for_each = {
    deploy = [
      "${local.repo_sub}:ref:refs/heads/main",
      "${local.repo_sub}:ref:refs/heads/gh-readonly-queue/main/*",
    ]
    # pull_request: per-PR previews. main: the nightly / manual preview reaper.
    preview = [
      "${local.repo_sub}:pull_request",
      "${local.repo_sub}:ref:refs/heads/main",
    ]
    readonly = ["${local.repo_sub}:*"]
  }

  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]

    principals {
      type        = "Federated"
      identifiers = [local.github_oidc_provider_arn]
    }

    condition {
      test     = "StringEquals"
      variable = "${local.github_oidc_host}:aud"
      values   = ["sts.amazonaws.com"]
    }

    condition {
      test     = "StringLike"
      variable = "${local.github_oidc_host}:sub"
      values   = each.value
    }
  }
}

locals {
  ecr_repo_arns = [for r in aws_ecr_repository.this : r.arn]
  cluster_arn   = "arn:${local.partition}:ecs:${var.region}:${local.account_id}:cluster/${local.cluster_name}"
  service_arns  = "arn:${local.partition}:ecs:${var.region}:${local.account_id}:service/${local.cluster_name}"
  ssm_arn       = "arn:${local.partition}:ssm:${var.region}:${local.account_id}:parameter/orders-demo/*"
  log_group_arn = "arn:${local.partition}:logs:${var.region}:${local.account_id}:log-group:/ecs/orders-demo/*"
  elb_prefix    = "arn:${local.partition}:elasticloadbalancing:${var.region}:${local.account_id}"
  role_prefix   = "arn:${local.partition}:iam::${local.account_id}:role/${local.ecs_role_prefix}"
}

# ---------------------------------------------------------------- deploy ----

resource "aws_iam_role" "deploy" {
  name                 = "orders-demo-deploy"
  description          = "GitHub Actions on main / merge queue: push images, deploy ECS"
  assume_role_policy   = data.aws_iam_policy_document.trust["deploy"].json
  max_session_duration = 3600
}

data "aws_iam_policy_document" "deploy" {
  statement {
    sid       = "EcrLogin"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    sid = "EcrPushPull"
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:BatchGetImage",
      "ecr:CompleteLayerUpload",
      "ecr:DescribeImages",
      "ecr:DescribeRepositories",
      "ecr:GetDownloadUrlForLayer",
      "ecr:InitiateLayerUpload",
      "ecr:ListImages",
      "ecr:PutImage",
      "ecr:UploadLayerPart",
    ]
    resources = local.ecr_repo_arns
  }

  statement {
    sid = "EcsTaskDefinitions"
    actions = [
      "ecs:DeregisterTaskDefinition",
      "ecs:DescribeTaskDefinition",
      "ecs:ListTaskDefinitions",
      "ecs:RegisterTaskDefinition",
      "ecs:TagResource",
    ]
    resources = ["*"]
  }

  statement {
    sid       = "EcsServices"
    actions   = ["ecs:DescribeServices", "ecs:UpdateService"]
    resources = ["${local.service_arns}/*"]
  }

  statement {
    sid       = "EcsRunOneOffTasks"
    actions   = ["ecs:RunTask"]
    resources = ["arn:${local.partition}:ecs:${var.region}:${local.account_id}:task-definition/orders-demo-*"]
    condition {
      test     = "ArnEquals"
      variable = "ecs:cluster"
      values   = [local.cluster_arn]
    }
  }

  statement {
    sid       = "EcsTasks"
    actions   = ["ecs:DescribeTasks", "ecs:StopTask"]
    resources = ["arn:${local.partition}:ecs:${var.region}:${local.account_id}:task/${local.cluster_name}/*"]
  }

  statement {
    sid = "EcsRead"
    actions = [
      "ecs:DescribeClusters",
      "ecs:DescribeServiceDeployments",
      "ecs:ListClusters",
      "ecs:ListServiceDeployments",
      "ecs:ListServices",
      "ecs:ListTasks",
    ]
    resources = ["*"]
  }

  statement {
    sid       = "PassTaskRoles"
    actions   = ["iam:PassRole"]
    resources = ["${local.role_prefix}*"]
    condition {
      test     = "StringEquals"
      variable = "iam:PassedToService"
      values   = ["ecs-tasks.amazonaws.com"]
    }
  }

  statement {
    sid       = "ReadDeployConfig"
    actions   = ["ssm:GetParameter", "ssm:GetParameters", "ssm:GetParametersByPath"]
    resources = [local.ssm_arn, replace(local.ssm_arn, "/*", "")]
  }

  statement {
    sid = "ReadAppConfig"
    actions = [
      "appconfig:GetApplication",
      "appconfig:GetConfiguration",
      "appconfig:GetConfigurationProfile",
      "appconfig:GetDeployment",
      "appconfig:GetEnvironment",
      "appconfig:GetHostedConfigurationVersion",
      "appconfig:GetLatestConfiguration",
      "appconfig:ListConfigurationProfiles",
      "appconfig:ListDeployments",
      "appconfig:ListEnvironments",
      "appconfig:ListHostedConfigurationVersions",
      "appconfig:StartConfigurationSession",
    ]
    resources = [
      aws_appconfig_application.orders.arn,
      "${aws_appconfig_application.orders.arn}/*",
    ]
  }

  statement {
    sid       = "ReadTaskLogs"
    actions   = ["logs:GetLogEvents", "logs:FilterLogEvents", "logs:DescribeLogStreams"]
    resources = [local.log_group_arn, "${local.log_group_arn}:*"]
  }

  statement {
    sid       = "DescribeLoadBalancing"
    actions   = ["elasticloadbalancing:Describe*"]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "deploy" {
  name   = "deploy"
  role   = aws_iam_role.deploy.id
  policy = data.aws_iam_policy_document.deploy.json
}

# --------------------------------------------------------------- preview ----

resource "aws_iam_role" "preview" {
  name                 = "orders-demo-preview"
  description          = "GitHub Actions on pull_request: pr-* images and pr-* ECS services"
  assume_role_policy   = data.aws_iam_policy_document.trust["preview"].json
  max_session_duration = 3600
}

data "aws_iam_policy_document" "preview" {
  statement {
    sid       = "EcrLogin"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  # ECR has no IAM condition key for image tags, so "only pr-* tags" cannot be
  # enforced here; the workflow tags pr-<N> and the lifecycle policy expires them.
  statement {
    sid = "EcrPush"
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:BatchGetImage",
      "ecr:CompleteLayerUpload",
      "ecr:DescribeImages",
      "ecr:GetDownloadUrlForLayer",
      "ecr:InitiateLayerUpload",
      "ecr:PutImage",
      "ecr:UploadLayerPart",
    ]
    resources = local.ecr_repo_arns
  }

  statement {
    sid = "TaskDefinitions"
    actions = [
      "ecs:DeregisterTaskDefinition",
      "ecs:DescribeTaskDefinition",
      "ecs:ListTaskDefinitions",
      "ecs:RegisterTaskDefinition",
      "ecs:TagResource",
    ]
    resources = ["*"]
  }

  statement {
    sid = "PrServices"
    actions = [
      "ecs:CreateService",
      "ecs:DeleteService",
      "ecs:DescribeServices",
      "ecs:UpdateService",
      "ecs:TagResource",
    ]
    resources = ["${local.service_arns}/pr-*"]
  }

  # The preview's one-off migration runs its own task definition with the
  # command overridden to ./efbundle.
  statement {
    sid       = "PrRunMigrations"
    actions   = ["ecs:RunTask"]
    resources = ["arn:${local.partition}:ecs:${var.region}:${local.account_id}:task-definition/orders-demo-pr-*"]
    condition {
      test     = "ArnEquals"
      variable = "ecs:cluster"
      values   = [local.cluster_arn]
    }
  }

  statement {
    sid       = "PrStopTasks"
    actions   = ["ecs:StopTask"]
    resources = ["arn:${local.partition}:ecs:${var.region}:${local.account_id}:task/${local.cluster_name}/*"]
  }

  statement {
    sid       = "ClusterRead"
    actions   = ["ecs:DescribeClusters", "ecs:ListServices", "ecs:ListTasks", "ecs:DescribeTasks"]
    resources = ["*"]
  }

  statement {
    sid       = "PassNonprodTaskRoles"
    actions   = ["iam:PassRole"]
    resources = ["${local.role_prefix}execution", "${local.role_prefix}task-nonprod"]
    condition {
      test     = "StringEquals"
      variable = "iam:PassedToService"
      values   = ["ecs-tasks.amazonaws.com"]
    }
  }

  statement {
    sid = "PrTargetGroups"
    actions = [
      "elasticloadbalancing:CreateTargetGroup",
      "elasticloadbalancing:DeleteTargetGroup",
      "elasticloadbalancing:ModifyTargetGroup",
      "elasticloadbalancing:ModifyTargetGroupAttributes",
    ]
    resources = ["${local.elb_prefix}:targetgroup/pr-*/*"]
  }

  # Listener rules have no name in their ARN, so they are scoped by tag:
  # created rules must carry preview=pr-*, and only such rules can be changed.
  statement {
    sid       = "CreatePrRules"
    actions   = ["elasticloadbalancing:CreateRule"]
    resources = ["${local.elb_prefix}:listener/app/orders-demo/*"]
    condition {
      test     = "StringLike"
      variable = "aws:RequestTag/preview"
      values   = ["pr-*"]
    }
  }

  statement {
    sid       = "ManagePrRules"
    actions   = ["elasticloadbalancing:DeleteRule", "elasticloadbalancing:ModifyRule", "elasticloadbalancing:SetRulePriorities"]
    resources = ["${local.elb_prefix}:listener-rule/app/orders-demo/*"]
    condition {
      test     = "StringLike"
      variable = "aws:ResourceTag/preview"
      values   = ["pr-*"]
    }
  }

  statement {
    sid     = "TagPrResources"
    actions = ["elasticloadbalancing:AddTags"]
    resources = [
      "${local.elb_prefix}:targetgroup/pr-*/*",
      "${local.elb_prefix}:listener-rule/app/orders-demo/*",
    ]
    condition {
      test     = "StringLike"
      variable = "aws:RequestTag/preview"
      values   = ["pr-*"]
    }
  }

  statement {
    sid       = "DescribeLoadBalancing"
    actions   = ["elasticloadbalancing:Describe*"]
    resources = ["*"]
  }

  statement {
    sid       = "ReadDeployConfig"
    actions   = ["ssm:GetParameter", "ssm:GetParameters", "ssm:GetParametersByPath"]
    resources = [local.ssm_arn, replace(local.ssm_arn, "/*", "")]
  }

  statement {
    sid       = "ReadPreviewLogs"
    actions   = ["logs:GetLogEvents", "logs:FilterLogEvents", "logs:DescribeLogStreams"]
    resources = ["${replace(local.log_group_arn, "*", "preview")}:*", replace(local.log_group_arn, "*", "preview")]
  }
}

resource "aws_iam_role_policy" "preview" {
  name   = "preview"
  role   = aws_iam_role.preview.id
  policy = data.aws_iam_policy_document.preview.json
}

# ----------------------------------------------------- incident readonly ----

resource "aws_iam_role" "incident_readonly" {
  name                 = "orders-demo-incident-readonly"
  description          = "Any workflow in the repo: read logs, metrics, alarms, ECS, X-Ray"
  assume_role_policy   = data.aws_iam_policy_document.trust["readonly"].json
  max_session_duration = 3600
}

data "aws_iam_policy_document" "incident_readonly" {
  statement {
    sid = "Observe"
    actions = [
      "cloudwatch:DescribeAlarmHistory",
      "cloudwatch:DescribeAlarms",
      "cloudwatch:DescribeAlarmsForMetric",
      "cloudwatch:GetMetricData",
      "cloudwatch:GetMetricStatistics",
      "cloudwatch:ListMetrics",
      "logs:DescribeLogGroups",
      "logs:DescribeLogStreams",
      "logs:FilterLogEvents",
      "logs:GetLogEvents",
      "logs:GetQueryResults",
      "logs:StartQuery",
      "logs:StopQuery",
      "ecs:DescribeClusters",
      "ecs:DescribeServices",
      "ecs:DescribeTaskDefinition",
      "ecs:DescribeTasks",
      "ecs:DescribeServiceDeployments",
      "ecs:ListClusters",
      "ecs:ListServiceDeployments",
      "ecs:ListServices",
      "ecs:ListTasks",
      "xray:BatchGetTraces",
      "xray:GetServiceGraph",
      "xray:GetTraceGraph",
      "xray:GetTraceSummaries",
      "xray:GetTimeSeriesServiceStatistics",
      "xray:GetInsightSummaries",
      "xray:GetGroups",
    ]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "incident_readonly" {
  name   = "incident-readonly"
  role   = aws_iam_role.incident_readonly.id
  policy = data.aws_iam_policy_document.incident_readonly.json
}
