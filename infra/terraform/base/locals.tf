locals {
  account_id = data.aws_caller_identity.current.account_id
  partition  = "aws"

  ecr_repositories       = ["orders-api", "orders-console", "orders-loadgen"]
  ecr_preview_repository = "orders-api-preview"

  # ECS task roles created by the env layer are all named orders-demo-ecs-*.
  # The deploy role may pass any of them; the preview role only the nonprod ones.
  ecs_role_prefix = "orders-demo-ecs-"

  cluster_name = "orders-demo"
}
