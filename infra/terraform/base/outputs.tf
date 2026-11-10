output "zone_id" {
  value = var.zone_id
}

output "domain" {
  value = var.domain
}

output "ecr_repository_urls" {
  value = merge(
    { for k, r in aws_ecr_repository.this : k => r.repository_url },
    { (aws_ecr_repository.preview.name) = aws_ecr_repository.preview.repository_url },
  )
}

output "github_role_arns" {
  value = {
    deploy            = aws_iam_role.deploy.arn
    release           = aws_iam_role.release.arn
    preview           = aws_iam_role.preview.arn
    incident_readonly = aws_iam_role.incident_readonly.arn
  }
}

output "ecs_role_prefix" {
  value = local.ecs_role_prefix
}

output "metrics_bucket" {
  value = aws_s3_bucket.metrics.bucket
}

output "demo_run_parameter" {
  value = aws_ssm_parameter.demo_run.name
}
