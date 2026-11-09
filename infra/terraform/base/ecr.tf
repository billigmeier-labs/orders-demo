# Release repositories: tags are immutable, so a commit-SHA tag can never be overwritten once the merge
# queue has pushed it. orders-loadgen excludes `latest` from immutability: CI moves it to the newest
# load generator (the one-off task definition runs :latest). PR images go to orders-api-preview.
# The pr- rule below is left over from when previews were pushed here (changing it replaces the policy).
resource "aws_ecr_repository" "this" {
  for_each = toset(local.ecr_repositories)

  name                 = each.key
  image_tag_mutability = each.key == "orders-loadgen" ? "IMMUTABLE_WITH_EXCLUSION" : "IMMUTABLE"
  force_delete         = true

  dynamic "image_tag_mutability_exclusion_filter" {
    for_each = each.key == "orders-loadgen" ? ["latest"] : []
    content {
      filter      = image_tag_mutability_exclusion_filter.value
      filter_type = "WILDCARD"
    }
  }

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "this" {
  for_each   = aws_ecr_repository.this
  repository = each.value.name

  policy = jsonencode({
    rules = [
      {
        rulePriority = 1
        description  = "Expire preview images after 7 days"
        selection = {
          tagStatus     = "tagged"
          tagPrefixList = ["pr-"]
          countType     = "sinceImagePushed"
          countUnit     = "days"
          countNumber   = 7
        }
        action = { type = "expire" }
      },
      {
        rulePriority = 2
        description  = "Keep the last 30 images"
        selection = {
          tagStatus   = "any"
          countType   = "imageCountMoreThan"
          countNumber = 30
        }
        action = { type = "expire" }
      },
    ]
  })
}

# Per-PR images (orders-api-preview:pr-<N>). Mutable: every push to a PR retags pr-<N>. The preview
# role can push only here, never to the release repositories.
resource "aws_ecr_repository" "preview" {
  name                 = local.ecr_preview_repository
  image_tag_mutability = "MUTABLE"
  force_delete         = true

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "preview" {
  repository = aws_ecr_repository.preview.name

  policy = jsonencode({
    rules = [
      {
        rulePriority = 1
        description  = "Expire preview images after 7 days"
        selection = {
          tagStatus   = "any"
          countType   = "sinceImagePushed"
          countUnit   = "days"
          countNumber = 7
        }
        action = { type = "expire" }
      },
    ]
  })
}
