# Alarms. All are named orders-demo-<service>-<kind>, so the EventBridge rule in eventbridge.tf forwards
# their state changes to GitHub (incident.yml) and the console lists them (ALARM_PREFIX).
#
# Per service (prod, staging and console):
#   -5xx          target 5xx responses per minute         } ECS deployment alarms: a deploy (and its bake)
#   -p95          target response time p95                } rolls back when one fires
# Per environment (api-prod, api-staging) as well:
#   -error-rate   5xx share of requests, metric math      } also a deployment alarm
#   -unhealthy    targets failing ALB health checks
#   -task-restarts tasks that crashed or failed health checks (EventBridge -> log group -> metric)

locals {
  api_services = toset(["api-prod", "api-staging"])

  alarms = merge([
    for k, s in local.services : {
      "${k}-5xx" = {
        service     = k
        metric      = "HTTPCode_Target_5XX_Count"
        statistic   = "Sum"
        threshold   = var.alarm_5xx_threshold
        description = "${k}: target 5xx responses per minute"
      }
      "${k}-p95" = {
        service     = k
        metric      = "TargetResponseTime"
        statistic   = null
        threshold   = var.alarm_p95_seconds
        description = "${k}: target response time p95 (seconds)"
      }
      "${k}-unhealthy" = {
        service     = k
        metric      = "UnHealthyHostCount"
        statistic   = "Maximum"
        threshold   = 0
        description = "${k}: targets failing the load balancer health check for 2 minutes"
      }
    }
  ]...)

  # The ones ECS watches during a deployment and its bake time.
  deployment_alarms = {
    for k, s in local.services : k => concat(
      [aws_cloudwatch_metric_alarm.svc["${k}-5xx"].alarm_name, aws_cloudwatch_metric_alarm.svc["${k}-p95"].alarm_name],
      contains(local.api_services, k) ? [aws_cloudwatch_metric_alarm.error_rate[k].alarm_name] : [],
    )
  }
}

resource "aws_cloudwatch_metric_alarm" "svc" {
  # The console's unhealthy alarm is left out: one task, so a restart is already the outage it reports.
  for_each = { for k, a in local.alarms : k => a if !(a.service == "console" && a.metric == "UnHealthyHostCount") }

  alarm_name          = "${local.name}-${each.key}"
  alarm_description   = each.value.description
  namespace           = "AWS/ApplicationELB"
  metric_name         = each.value.metric
  statistic           = each.value.statistic
  extended_statistic  = each.value.statistic == null ? "p95" : null
  period              = 60
  evaluation_periods  = 2
  datapoints_to_alarm = 2
  threshold           = each.value.threshold
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"

  dimensions = {
    LoadBalancer = aws_lb.this.arn_suffix
    TargetGroup  = aws_lb_target_group.svc[each.value.service].arn_suffix
  }
}

# Error rate: 5xx as a percentage of requests, two one-minute datapoints in a row. Minutes with fewer
# than alarm_min_requests requests count as 0 % so a couple of errors on an idle service stay quiet.
resource "aws_cloudwatch_metric_alarm" "error_rate" {
  for_each = local.api_services

  alarm_name          = "${local.name}-${each.key}-error-rate"
  alarm_description   = "${each.key}: more than ${var.alarm_error_rate_percent}% of requests answered 5xx (minutes with at least ${var.alarm_min_requests} requests)"
  evaluation_periods  = 2
  datapoints_to_alarm = 2
  threshold           = var.alarm_error_rate_percent
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"

  metric_query {
    id          = "rate"
    label       = "5xx % of requests"
    expression  = "IF(FILL(requests, 0) >= ${var.alarm_min_requests}, 100 * FILL(errors, 0) / requests, 0)"
    return_data = true
  }

  metric_query {
    id = "errors"
    metric {
      namespace   = "AWS/ApplicationELB"
      metric_name = "HTTPCode_Target_5XX_Count"
      stat        = "Sum"
      period      = 60
      dimensions = {
        LoadBalancer = aws_lb.this.arn_suffix
        TargetGroup  = aws_lb_target_group.svc[each.key].arn_suffix
      }
    }
  }

  metric_query {
    id = "requests"
    metric {
      namespace   = "AWS/ApplicationELB"
      metric_name = "RequestCount"
      stat        = "Sum"
      period      = 60
      dimensions = {
        LoadBalancer = aws_lb.this.arn_suffix
        TargetGroup  = aws_lb_target_group.svc[each.key].arn_suffix
      }
    }
  }
}

# ------------------------------------------------------------ task restarts ----
# ECS has no "task restarted" metric without Container Insights, so ECS task state changes for service
# tasks that stopped for a bad reason (essential container exited, failed to start, failed health checks)
# are written to a log group, and a metric filter counts them per service: OrdersDemo TaskStops{group}.
# Tasks replaced by a normal deployment or scaled in (ServiceSchedulerInitiated "Scaling activity") don't count.

resource "aws_cloudwatch_log_group" "task_stops" {
  name              = "/aws/events/${local.name}/task-stops"
  retention_in_days = 7
}

data "aws_iam_policy_document" "task_stops_logs" {
  statement {
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["${aws_cloudwatch_log_group.task_stops.arn}:*"]
    principals {
      type        = "Service"
      identifiers = ["events.amazonaws.com", "delivery.logs.amazonaws.com"]
    }
  }
}

resource "aws_cloudwatch_log_resource_policy" "task_stops" {
  policy_name     = "${local.name}-task-stops"
  policy_document = data.aws_iam_policy_document.task_stops_logs.json
}

resource "aws_cloudwatch_event_rule" "task_stops" {
  name        = "${local.name}-task-stops"
  description = "orders-demo service tasks that crashed, failed to start or failed health checks"

  event_pattern = jsonencode({
    source        = ["aws.ecs"]
    "detail-type" = ["ECS Task State Change"]
    detail = {
      clusterArn = [aws_ecs_cluster.this.arn]
      lastStatus = ["STOPPED"]
      group      = [for k in keys(local.services) : "service:${k}"]
      "$or" = [
        { stopCode = ["EssentialContainerExited", "TaskFailedToStart"] },
        { stoppedReason = [{ prefix = "Task failed ELB health checks" }, { prefix = "Task failed container health checks" }] },
      ]
    }
  })
}

resource "aws_cloudwatch_event_target" "task_stops" {
  rule = aws_cloudwatch_event_rule.task_stops.name
  arn  = aws_cloudwatch_log_group.task_stops.arn

  depends_on = [aws_cloudwatch_log_resource_policy.task_stops]
}

resource "aws_cloudwatch_log_metric_filter" "task_stops" {
  name           = "${local.name}-task-stops"
  log_group_name = aws_cloudwatch_log_group.task_stops.name
  pattern        = "{ $.detail.lastStatus = \"STOPPED\" }"

  metric_transformation {
    name       = "TaskStops"
    namespace  = "OrdersDemo"
    value      = "1"
    unit       = "Count"
    dimensions = { group = "$.detail.group" }
  }
}

resource "aws_cloudwatch_metric_alarm" "task_restarts" {
  for_each = local.api_services

  alarm_name          = "${local.name}-${each.key}-task-restarts"
  alarm_description   = "${each.key}: a task crashed, failed to start or failed its health checks (last 5 minutes)"
  namespace           = "OrdersDemo"
  metric_name         = "TaskStops"
  statistic           = "Sum"
  period              = 60
  evaluation_periods  = 5
  datapoints_to_alarm = 1
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"

  dimensions = {
    group = "service:${each.key}"
  }

  depends_on = [aws_cloudwatch_log_metric_filter.task_stops]
}
