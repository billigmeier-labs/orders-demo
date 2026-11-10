# One CloudWatch dashboard per environment: orders-demo-prod and orders-demo-staging. Load balancer
# metrics (what the alarms use), the per-endpoint metrics the collector sidecars send (OrdersDemo
# RequestDuration), ECS and task restarts, and the latest errors from the API's log group.

locals {
  dashboards = {
    prod    = "api-prod"
    staging = "api-staging"
  }
}

resource "aws_cloudwatch_dashboard" "env" {
  for_each = local.dashboards

  dashboard_name = "${local.name}-${each.key}"
  dashboard_body = jsonencode({
    start          = "-PT3H"
    periodOverride = "inherit"
    widgets = [
      {
        type = "alarm", x = 0, y = 0, width = 24, height = 3
        properties = {
          title = "Alarms (${each.key})"
          alarms = concat(
            [for k in ["5xx", "p95", "unhealthy"] : aws_cloudwatch_metric_alarm.svc["${each.value}-${k}"].arn],
            [aws_cloudwatch_metric_alarm.error_rate[each.value].arn, aws_cloudwatch_metric_alarm.task_restarts[each.value].arn],
          )
        }
      },
      {
        type = "metric", x = 0, y = 3, width = 8, height = 6
        properties = {
          title  = "Requests and 5xx per minute"
          region = var.region
          stat   = "Sum"
          period = 60
          view   = "timeSeries"
          metrics = [
            ["AWS/ApplicationELB", "RequestCount", "TargetGroup", aws_lb_target_group.svc[each.value].arn_suffix, "LoadBalancer", aws_lb.this.arn_suffix, { label = "Requests" }],
            [".", "HTTPCode_Target_5XX_Count", ".", ".", ".", ".", { label = "Target 5xx", color = "#d62728" }],
            [".", "HTTPCode_Target_4XX_Count", ".", ".", ".", ".", { label = "Target 4xx", color = "#ff7f0e" }],
          ]
        }
      },
      {
        type = "metric", x = 8, y = 3, width = 8, height = 6
        properties = {
          title  = "Error rate (% of requests answered 5xx)"
          region = var.region
          period = 60
          view   = "timeSeries"
          yAxis  = { left = { min = 0 } }
          metrics = [
            [{ expression = "100 * FILL(e, 0) / r", label = "5xx %", id = "rate" }],
            ["AWS/ApplicationELB", "HTTPCode_Target_5XX_Count", "TargetGroup", aws_lb_target_group.svc[each.value].arn_suffix, "LoadBalancer", aws_lb.this.arn_suffix, { id = "e", stat = "Sum", visible = false }],
            [".", "RequestCount", ".", ".", ".", ".", { id = "r", stat = "Sum", visible = false }],
          ]
          annotations = { horizontal = [{ label = "Alarm", value = var.alarm_error_rate_percent }] }
        }
      },
      {
        type = "metric", x = 16, y = 3, width = 8, height = 6
        properties = {
          title  = "Response time (seconds)"
          region = var.region
          period = 60
          view   = "timeSeries"
          metrics = [
            ["AWS/ApplicationELB", "TargetResponseTime", "TargetGroup", aws_lb_target_group.svc[each.value].arn_suffix, "LoadBalancer", aws_lb.this.arn_suffix, { stat = "p50", label = "p50" }],
            ["...", { stat = "p95", label = "p95" }],
            ["...", { stat = "p99", label = "p99" }],
          ]
          annotations = { horizontal = [{ label = "p95 alarm", value = var.alarm_p95_seconds }] }
        }
      },
      {
        type = "metric", x = 0, y = 9, width = 8, height = 6
        properties = {
          title   = "Requests per endpoint (per minute)"
          region  = var.region
          period  = 60
          view    = "timeSeries"
          stacked = true
          metrics = [[{ expression = "SEARCH('{OrdersDemo,endpoint,environment,service} MetricName=\"RequestDuration\" environment=\"${each.key}\"', 'SampleCount', 60)", id = "req", label = "" }]]
        }
      },
      {
        type = "metric", x = 8, y = 9, width = 8, height = 6
        properties = {
          title   = "5xx per endpoint (per minute)"
          region  = var.region
          period  = 60
          view    = "timeSeries"
          metrics = [[{ expression = "SEARCH('{OrdersDemo,endpoint,environment,service,status_class} MetricName=\"RequestDuration\" environment=\"${each.key}\" status_class=\"5xx\"', 'SampleCount', 60)", id = "err", label = "" }]]
        }
      },
      {
        type = "metric", x = 16, y = 9, width = 8, height = 6
        properties = {
          title   = "p95 per endpoint (seconds)"
          region  = var.region
          period  = 60
          view    = "timeSeries"
          metrics = [[{ expression = "SEARCH('{OrdersDemo,endpoint,environment,service} MetricName=\"RequestDuration\" environment=\"${each.key}\"', 'p95', 60)", id = "p95", label = "" }]]
        }
      },
      {
        type = "metric", x = 0, y = 15, width = 8, height = 6
        properties = {
          title  = "Targets"
          region = var.region
          period = 60
          view   = "timeSeries"
          metrics = [
            ["AWS/ApplicationELB", "HealthyHostCount", "TargetGroup", aws_lb_target_group.svc[each.value].arn_suffix, "LoadBalancer", aws_lb.this.arn_suffix, { stat = "Minimum", label = "Healthy" }],
            [".", "UnHealthyHostCount", ".", ".", ".", ".", { stat = "Maximum", label = "Unhealthy", color = "#d62728" }],
            ["OrdersDemo", "TaskStops", "group", "service:${each.value}", { stat = "Sum", label = "Task restarts", color = "#9467bd" }],
          ]
        }
      },
      {
        type = "metric", x = 8, y = 15, width = 8, height = 6
        properties = {
          title  = "ECS CPU and memory (%)"
          region = var.region
          period = 60
          view   = "timeSeries"
          metrics = [
            ["AWS/ECS", "CPUUtilization", "ClusterName", aws_ecs_cluster.this.name, "ServiceName", each.value, { stat = "Average", label = "CPU" }],
            [".", "MemoryUtilization", ".", ".", ".", ".", { stat = "Average", label = "Memory" }],
          ]
        }
      },
      {
        type = "log", x = 16, y = 15, width = 8, height = 6
        properties = {
          title  = "Latest errors"
          region = var.region
          view   = "table"
          query  = "SOURCE '${aws_cloudwatch_log_group.this[each.value].name}' | fields @timestamp, @message | filter @message like /\"(Error|Fatal)\"|FaultInjected|Exception/ | sort @timestamp desc | limit 20"
        }
      },
    ]
  })
}
