# What survives between demos besides the roles: the demo-run counter and the metrics history.

# The current demo run N. `just env up` increments it when it builds a fresh environment; every env-layer
# resource is tagged demo-run=<N> (resources here are tagged demo-run=persistent). The console reads it
# to label its per-run snapshot. Terraform only creates it; the value belongs to `just env up`.
resource "aws_ssm_parameter" "demo_run" {
  name        = "/orders-demo/demo-run"
  description = "Current demo run number (set by just env up)"
  type        = "String"
  # The environment that was up when this parameter was introduced counts as run 1.
  value = "1"

  lifecycle {
    ignore_changes = [value]
  }
}

# Per-run metric snapshots (the console writes runs/<N>.json) so runs can be compared after teardown.
resource "aws_s3_bucket" "metrics" {
  bucket = local.metrics_bucket
  # Never emptied by Terraform: the history is the point. `just env nuke` stops here if it has objects.
  force_destroy = false
}

resource "aws_s3_bucket_public_access_block" "metrics" {
  bucket                  = aws_s3_bucket.metrics.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "metrics" {
  bucket = aws_s3_bucket.metrics.id
  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "metrics" {
  bucket = aws_s3_bucket.metrics.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_ssm_parameter" "metrics_bucket" {
  name        = "/orders-demo/metrics-bucket"
  description = "S3 bucket for per-run metric snapshots (runs/<N>.json)"
  type        = "String"
  value       = aws_s3_bucket.metrics.bucket
}
