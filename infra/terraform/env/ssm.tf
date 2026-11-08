# Deploy-time facts for GitHub workflows (deploy + preview roles read
# /orders-demo/*), so workflows never hard-code IDs that change per `up`.
locals {
  ssm = merge(
    {
      "cluster"                 = aws_ecs_cluster.this.name
      "subnets"                 = join(",", local.subnet_ids)
      "task-security-group"     = aws_security_group.tasks.id
      "vpc-id"                  = var.vpc_id
      "https-listener-arn"      = aws_lb_listener.https.arn
      "execution-role-arn"      = aws_iam_role.execution.arn
      "task-role-arn/prod"      = aws_iam_role.task["task-prod"].arn
      "task-role-arn/nonprod"   = aws_iam_role.task["task-nonprod"].arn
      "preview/log-group"       = aws_cloudwatch_log_group.this["preview"].name
      "preview/db-host"         = aws_db_instance.this["nonprod"].address
      "preview/db-username"     = aws_db_instance.this["nonprod"].username
      "preview/db-password-ref" = local.db_password_secret["nonprod"]
    },
    { for k, s in local.services : "services/${k}/desired-count" => tostring(s.desired) },
    { for k, td in aws_ecs_task_definition.migrate : "migrate/${k}/family" => td.family },
    { "loadgen/family" = aws_ecs_task_definition.loadgen.family },
  )
}

resource "aws_ssm_parameter" "deploy" {
  for_each = local.ssm
  name     = "/${local.name}/${each.key}"
  type     = "String"
  value    = each.value
}
