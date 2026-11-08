output "certificate_arn" {
  value = aws_acm_certificate_validation.demo.certificate_arn
}

output "zone_id" {
  value = var.zone_id
}

output "domain" {
  value = var.domain
}

output "ecr_repository_urls" {
  value = { for k, r in aws_ecr_repository.this : k => r.repository_url }
}

output "github_role_arns" {
  value = {
    deploy            = aws_iam_role.deploy.arn
    preview           = aws_iam_role.preview.arn
    incident_readonly = aws_iam_role.incident_readonly.arn
  }
}

output "ecs_role_prefix" {
  value = local.ecs_role_prefix
}

output "appconfig" {
  value = {
    application_id   = aws_appconfig_application.orders.id
    application_arn  = aws_appconfig_application.orders.arn
    profile_id       = aws_appconfig_configuration_profile.flags.configuration_profile_id
    environment_ids  = { for k, e in aws_appconfig_environment.this : k => e.environment_id }
    environment_arns = { for k, e in aws_appconfig_environment.this : k => e.arn }
  }
}

output "appconfig_deployment_strategy_id" {
  value = aws_appconfig_deployment_strategy.instant.id
}

output "console_secret_arn" {
  value = aws_secretsmanager_secret.console.arn
}
