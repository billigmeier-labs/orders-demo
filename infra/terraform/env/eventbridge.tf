# Alarm state changes for orders-demo-* alarms -> GitHub repository_dispatch
# (event_type "alarm"). OK is only sent when recovering from ALARM, so new
# alarms leaving INSUFFICIENT_DATA don't dispatch.

resource "aws_cloudwatch_event_connection" "github" {
  name               = "${local.name}-github"
  description        = "GitHub API (repository_dispatch)"
  authorization_type = "API_KEY"

  auth_parameters {
    api_key {
      key   = "Authorization"
      value = "Bearer ${var.github_dispatch_token}"
    }

    invocation_http_parameters {
      header {
        key   = "Accept"
        value = "application/vnd.github+json"
      }
      header {
        key   = "X-GitHub-Api-Version"
        value = "2022-11-28"
      }
    }
  }
}

resource "aws_cloudwatch_event_api_destination" "github_dispatch" {
  name                             = "${local.name}-github-dispatch"
  connection_arn                   = aws_cloudwatch_event_connection.github.arn
  invocation_endpoint              = "https://api.github.com/repos/${var.github_repo}/dispatches"
  http_method                      = "POST"
  invocation_rate_limit_per_second = 1
}

resource "aws_cloudwatch_event_rule" "alarms" {
  name        = "${local.name}-alarm-state"
  description = "orders-demo alarm state changes to GitHub"

  event_pattern = jsonencode({
    source        = ["aws.cloudwatch"]
    "detail-type" = ["CloudWatch Alarm State Change"]
    detail = {
      alarmName = [{ prefix = "${local.name}-" }]
      "$or" = [
        { state = { value = ["ALARM"] } },
        { state = { value = ["OK"] }, previousState = { value = ["ALARM"] } },
      ]
    }
  })
}

data "aws_iam_policy_document" "events_trust" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["events.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "events" {
  name               = "${local.name}-events-github"
  assume_role_policy = data.aws_iam_policy_document.events_trust.json
}

resource "aws_iam_role_policy" "events" {
  name = "invoke-github-dispatch"
  role = aws_iam_role.events.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = "events:InvokeApiDestination"
      Resource = aws_cloudwatch_event_api_destination.github_dispatch.arn
    }]
  })
}

resource "aws_cloudwatch_event_target" "github_dispatch" {
  rule     = aws_cloudwatch_event_rule.alarms.name
  arn      = aws_cloudwatch_event_api_destination.github_dispatch.arn
  role_arn = aws_iam_role.events.arn

  input_transformer {
    input_paths = {
      alarmName     = "$.detail.alarmName"
      state         = "$.detail.state.value"
      previousState = "$.detail.previousState.value"
      reason        = "$.detail.state.reason"
      time          = "$.time"
      region        = "$.region"
      account       = "$.account"
    }
    input_template = <<-JSON
      {"event_type":"alarm","client_payload":{"alarmName":<alarmName>,"state":<state>,"previousState":<previousState>,"reason":<reason>,"time":<time>,"region":<region>,"account":<account>}}
    JSON
  }

  retry_policy {
    maximum_event_age_in_seconds = 3600
    maximum_retry_attempts       = 10
  }
}
