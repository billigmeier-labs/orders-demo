locals {
  account_id = data.aws_caller_identity.current.account_id
  partition  = "aws"

  ecr_repositories       = ["orders-api", "orders-console", "orders-loadgen"]
  ecr_preview_repository = "orders-api-preview"

  # ECS task roles created by the env layer are all named orders-demo-ecs-*.
  # The deploy role may pass any of them; the preview role only these two (env layer iam.tf),
  # which reach the nonprod database secret, the preview log group and preview AppConfig only.
  ecs_role_prefix        = "orders-demo-ecs-"
  preview_execution_role = "preview-execution"
  preview_task_role      = "preview-task"

  cluster_name = "orders-demo"
}
