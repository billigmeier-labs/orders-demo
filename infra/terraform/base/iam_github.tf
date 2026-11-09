# The GitHub OIDC provider is owned by another repo (created by the
# orchestrator); look it up by URL. It must exist before this layer plans.
data "aws_iam_openid_connect_provider" "github" {
  url = "https://token.actions.githubusercontent.com"
}

locals {
  github_oidc_host         = "token.actions.githubusercontent.com"
  github_oidc_provider_arn = data.aws_iam_openid_connect_provider.github.arn
  # GitHub issues this org's OIDC tokens with the immutable subject format (owner and repo IDs),
  # e.g. repo:billigmeier-labs@354132701/orders-demo@1450435209:ref:refs/heads/main. A job that names
  # a GitHub environment gets repo:<owner@id>/<repo@id>:environment:<name> instead of the ref.
  repo_sub = "repo:${var.github_repo_immutable}"
  env_sub  = "${local.repo_sub}:environment"
}

# Roles that can change what runs (push release images, update services) trust only GitHub
# environments, never a branch: any workflow on main gets ref:refs/heads/main, including the
# issue-triggered agent and triage workflows where an AI runs shell commands. Each environment's
# deployment branch policy (GitHub side) decides which refs may use it:
#   staging, production, preview-reaper: main only
#   release: main and gh-readonly-queue/main/* (the merge queue builds the release images)
#
# Where it is known, the trust also pins job_workflow_ref (owner/repo/<workflow path>@<ref>, name based
# even with the immutable subject; for a job in a reusable workflow it is the called workflow), so a
# different workflow file that names the same environment, or runs on main, cannot assume the role.
# The preview role cannot be pinned this way: on pull_request the PR's own copy of the workflow runs
# (and its job_workflow_ref is the PR's merge ref), so its permissions are the boundary instead.
locals {
  workflow_ref = "${var.github_repo}/.github/workflows"

  trust = {
    # deploy-service.yml (called by deploy.yml): staging and production (the console deploys as production).
    deploy = {
      subs      = ["${local.env_sub}:staging", "${local.env_sub}:production"]
      workflows = ["${local.workflow_ref}/deploy-service.yml@refs/heads/main"]
    }
    # ci.yml release image job (merge queue, push to main) and deploy.yml's Prepare job.
    release = {
      subs = ["${local.env_sub}:release"]
      workflows = [
        "${local.workflow_ref}/ci.yml@refs/heads/main",
        "${local.workflow_ref}/ci.yml@refs/heads/gh-readonly-queue/main/*",
        "${local.workflow_ref}/deploy.yml@refs/heads/main",
      ]
    }
    # pull_request: per-PR previews. preview-reaper: the nightly / manual reaper on main.
    preview = {
      subs      = ["${local.repo_sub}:pull_request", "${local.env_sub}:preview-reaper"]
      workflows = []
    }
    # incident.yml (repository_dispatch and workflow_dispatch) on main only.
    readonly = {
      subs      = ["${local.repo_sub}:ref:refs/heads/main"]
      workflows = ["${local.workflow_ref}/incident.yml@refs/heads/main"]
    }
  }
}

data "aws_iam_policy_document" "trust" {
  for_each = local.trust

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
      values   = each.value.subs
    }

    dynamic "condition" {
      for_each = length(each.value.workflows) > 0 ? [each.value.workflows] : []
      content {
        test     = "StringLike"
        variable = "${local.github_oidc_host}:job_workflow_ref"
        values   = condition.value
      }
    }
  }
}

locals {
  ecr_repo_arns   = [for r in aws_ecr_repository.this : r.arn]
  ecr_preview_arn = aws_ecr_repository.preview.arn
  cluster_arn     = "arn:${local.partition}:ecs:${var.region}:${local.account_id}:cluster/${local.cluster_name}"
  service_arns    = "arn:${local.partition}:ecs:${var.region}:${local.account_id}:service/${local.cluster_name}"
  ssm_arn         = "arn:${local.partition}:ssm:${var.region}:${local.account_id}:parameter/orders-demo/*"
  log_group_arn   = "arn:${local.partition}:logs:${var.region}:${local.account_id}:log-group:/ecs/orders-demo/*"
  elb_prefix      = "arn:${local.partition}:elasticloadbalancing:${var.region}:${local.account_id}"
  role_prefix     = "arn:${local.partition}:iam::${local.account_id}:role/${local.ecs_role_prefix}"
  task_def_prefix = "arn:${local.partition}:ecs:${var.region}:${local.account_id}:task-definition/"
  task_arns       = "arn:${local.partition}:ecs:${var.region}:${local.account_id}:task/${local.cluster_name}"
}

# ---------------------------------------------------------------- deploy ----

resource "aws_iam_role" "deploy" {
  name                 = "orders-demo-deploy"
  description          = "GitHub Actions environments staging/production: deploy ECS services"
  assume_role_policy   = data.aws_iam_policy_document.trust["deploy"].json
  max_session_duration = 3600
}

data "aws_iam_policy_document" "deploy" {
  statement {
    sid       = "EcrLogin"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  # Read only: release images are pushed by the release role, deploys only point at them.
  statement {
    sid = "EcrRead"
    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:BatchGetImage",
      "ecr:DescribeImages",
      "ecr:DescribeRepositories",
      "ecr:GetDownloadUrlForLayer",
      "ecr:ListImages",
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

# --------------------------------------------------------------- release ----

resource "aws_iam_role" "release" {
  name                 = "orders-demo-release"
  description          = "GitHub Actions environment release: push release images, read deploy preflight"
  assume_role_policy   = data.aws_iam_policy_document.trust["release"].json
  max_session_duration = 3600
}

data "aws_iam_policy_document" "release" {
  statement {
    sid       = "EcrLogin"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  # Release repositories are IMMUTABLE (orders-loadgen except :latest), so a pushed SHA tag stays put.
  statement {
    sid = "EcrPushRelease"
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

  # deploy.yml Prepare: is the environment up, which console version is running.
  statement {
    sid       = "ReadDeployConfig"
    actions   = ["ssm:GetParameter", "ssm:GetParameters"]
    resources = [local.ssm_arn]
  }

  statement {
    sid       = "EcsRead"
    actions   = ["ecs:DescribeServices", "ecs:DescribeTaskDefinition"]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "release" {
  name   = "release"
  role   = aws_iam_role.release.id
  policy = data.aws_iam_policy_document.release.json
}

# --------------------------------------------------------------- preview ----

resource "aws_iam_role" "preview" {
  name                 = "orders-demo-preview"
  description          = "GitHub Actions on pull_request (and env preview-reaper): orders-api-preview images, pr-* ECS services"
  assume_role_policy   = data.aws_iam_policy_document.trust["preview"].json
  max_session_duration = 3600
}

data "aws_iam_policy_document" "preview" {
  statement {
    sid       = "EcrLogin"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  # Only the preview repository: a PR can never overwrite a release image.
  statement {
    sid = "EcrPushPreview"
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
    resources = [local.ecr_preview_arn]
  }

  # Everything below is reachable from any same-repo PR (it runs its own copy of preview.yml), so the
  # policy itself keeps previews away from production: only orders-demo-pr-* task definitions,
  # services and tasks, and only the preview execution and task roles (nonprod secret, preview log
  # group, preview AppConfig). RegisterTaskDefinition is scoped by family so a PR cannot add a
  # revision to a prod family that the next deploy would copy.
  statement {
    sid       = "RegisterPrTaskDefinitions"
    actions   = ["ecs:RegisterTaskDefinition"]
    resources = ["${local.task_def_prefix}orders-demo-pr-*"]
  }

  # No resource-level permissions for these (Service Authorization Reference).
  statement {
    sid = "TaskDefinitionsRead"
    actions = [
      "ecs:DeregisterTaskDefinition",
      "ecs:DescribeTaskDefinition",
      "ecs:ListTaskDefinitions",
    ]
    resources = ["*"]
  }

  # Tags only on resources being created (register, create-service, run-task), and they must carry
  # preview=pr-*: an existing (prod) task can never be tagged into the PrStopTasks scope.
  statement {
    sid     = "TagPrResourcesOnCreate"
    actions = ["ecs:TagResource"]
    resources = [
      "${local.task_def_prefix}orders-demo-pr-*",
      "${local.service_arns}/pr-*",
      "${local.task_arns}/*",
    ]
    condition {
      test     = "StringEquals"
      variable = "ecs:CreateAction"
      values   = ["RegisterTaskDefinition", "CreateService", "RunTask"]
    }
    condition {
      test     = "StringLike"
      variable = "aws:RequestTag/preview"
      values   = ["pr-*"]
    }
  }

  # A pr-* service may only run a pr-* task definition (not, say, orders-demo-api-prod).
  statement {
    sid       = "PrServicesWrite"
    actions   = ["ecs:CreateService", "ecs:UpdateService"]
    resources = ["${local.service_arns}/pr-*"]
    condition {
      test     = "ArnLike"
      variable = "ecs:task-definition"
      values   = ["${local.task_def_prefix}orders-demo-pr-*"]
    }
  }

  statement {
    sid       = "PrServicesManage"
    actions   = ["ecs:DeleteService", "ecs:DescribeServices"]
    resources = ["${local.service_arns}/pr-*"]
  }

  # The preview's one-off migration runs its own task definition with the
  # command overridden to ./efbundle.
  statement {
    sid       = "PrRunMigrations"
    actions   = ["ecs:RunTask"]
    resources = ["${local.task_def_prefix}orders-demo-pr-*"]
    condition {
      test     = "ArnEquals"
      variable = "ecs:cluster"
      values   = [local.cluster_arn]
    }
  }

  # StopTask has no task-definition condition key; preview tasks carry preview=pr-<N> (run-task --tags,
  # services --propagate-tags SERVICE).
  statement {
    sid       = "PrStopTasks"
    actions   = ["ecs:StopTask"]
    resources = ["${local.task_arns}/*"]
    condition {
      test     = "StringLike"
      variable = "aws:ResourceTag/preview"
      values   = ["pr-*"]
    }
  }

  statement {
    sid       = "ClusterRead"
    actions   = ["ecs:DescribeClusters", "ecs:ListServices", "ecs:ListTasks", "ecs:DescribeTasks"]
    resources = ["*"]
  }

  # Only the preview roles (env layer): never orders-demo-ecs-execution, which can read the prod
  # database and console secrets.
  statement {
    sid       = "PassPreviewTaskRoles"
    actions   = ["iam:PassRole"]
    resources = ["${local.role_prefix}${local.preview_execution_role}", "${local.role_prefix}${local.preview_task_role}"]
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
  description          = "incident.yml on main: read logs, metrics, alarms, ECS, X-Ray"
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
