locals {
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
    }
  ]...)
}

# Named orders-demo-* so the EventBridge rule below forwards them to GitHub.
resource "aws_cloudwatch_metric_alarm" "svc" {
  for_each = local.alarms

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
