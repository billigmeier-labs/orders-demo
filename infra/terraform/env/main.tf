data "terraform_remote_state" "base" {
  backend = "s3"
  config = {
    bucket = "joshb-terraform-state-983889169993"
    key    = "orders-demo/base.tfstate"
    region = "us-east-1"
  }
}

data "aws_subnets" "public" {
  filter {
    name   = "vpc-id"
    values = [var.vpc_id]
  }
  filter {
    name   = "default-for-az"
    values = ["true"]
  }
  filter {
    name   = "availability-zone"
    values = var.availability_zones
  }
}

locals {
  base       = data.terraform_remote_state.base.outputs
  account_id = data.aws_caller_identity.current.account_id
  name       = "orders-demo"
  domain     = local.base.domain
  subnet_ids = sort(data.aws_subnets.public.ids)
  ecr        = local.base.ecr_repository_urls
  role_pfx   = local.base.ecs_role_prefix
  appconfig  = local.base.appconfig

  # Long-running services. desired is what the service runs once an image exists. bake_minutes is how
  # long ECS keeps watching the deployment alarms after the new tasks are healthy: staging is short so
  # merge to prod stays quick; prod keeps 5 minutes so a bad version under real traffic still rolls back.
  services = {
    api-prod = {
      host         = local.domain
      repo         = "orders-api"
      tag          = var.api_image_tag
      desired      = 2
      health_path  = "/health/ready"
      env          = "prod"
      db           = "prod"
      task_role    = "task-prod"
      priority     = 10
      bake_minutes = 5
    }
    api-staging = {
      host         = "staging.${local.domain}"
      repo         = "orders-api"
      tag          = var.api_image_tag
      desired      = 1
      health_path  = "/health/ready"
      env          = "staging"
      db           = "nonprod"
      task_role    = "task-nonprod"
      priority     = 20
      bake_minutes = 1
    }
    console = {
      host         = "console.${local.domain}"
      repo         = "orders-console"
      tag          = var.console_image_tag
      desired      = 1
      health_path  = "/health/live"
      env          = "prod"
      db           = null
      task_role    = "console"
      priority     = 30
      bake_minutes = 5
    }
  }
}
