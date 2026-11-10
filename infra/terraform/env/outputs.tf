output "alb_dns_name" {
  value = aws_lb.this.dns_name
}

output "cluster" {
  value = aws_ecs_cluster.this.name
}

output "services" {
  value = { for k, s in aws_ecs_service.svc : k => { name = s.name, desired_count = local.services[k].desired, url = "https://${local.services[k].host}" } }
}

output "subnet_ids" {
  value = local.subnet_ids
}

output "task_security_group_id" {
  value = aws_security_group.tasks.id
}

output "preview_security_group_id" {
  value = aws_security_group.previews.id
}

output "https_listener_arn" {
  value = aws_lb_listener.https.arn
}

output "task_definition_families" {
  value = merge(
    { for k, td in aws_ecs_task_definition.svc : k => td.family },
    { for k, td in aws_ecs_task_definition.migrate : "migrate-${k}" => td.family },
    { loadgen = aws_ecs_task_definition.loadgen.family },
  )
}

output "db_endpoints" {
  value = { for k, db in aws_db_instance.this : k => db.address }
}

output "urls" {
  value = {
    prod    = "https://${local.domain}"
    staging = "https://staging.${local.domain}"
    console = "https://console.${local.domain}"
    dashboards = {
      for k, d in aws_cloudwatch_dashboard.env : k => "https://${var.region}.console.aws.amazon.com/cloudwatch/home?region=${var.region}#dashboards/dashboard/${d.dashboard_name}"
    }
  }
}

output "console_secret_arn" {
  value = aws_secretsmanager_secret.console.arn
}

output "demo_run" {
  value = var.demo_run
}
