variable "region" {
  type    = string
  default = "us-east-1"
}

variable "vpc_id" {
  description = "Default VPC."
  type        = string
  default     = "vpc-2b62354d"
}

variable "availability_zones" {
  description = "AZs to use. us-east-1e (use1-az3) is skipped: Fargate does not run there."
  type        = list(string)
  default     = ["us-east-1a", "us-east-1b", "us-east-1c"]
}

variable "api_image_tag" {
  description = "orders-api image tag used when services are first created. Empty = desired count 0."
  type        = string
  default     = ""
}

variable "console_image_tag" {
  description = "orders-console image tag used when the service is first created. Empty = desired count 0."
  type        = string
  default     = ""
}

variable "loadgen_image_tag" {
  description = "orders-loadgen image tag for the loadgen task definition (RunTask can override)."
  type        = string
  default     = "latest"
}

variable "migrate_command" {
  description = "Entry point of the migrate task (runs the EF Core migration bundle shipped in the api image)."
  type        = list(string)
  default     = ["./efbundle"]
}

variable "github_repo" {
  type    = string
  default = "billigmeier-labs/orders-demo"
}

variable "github_dispatch_token" {
  description = "GitHub token allowed to create repository_dispatch events. Supply via TF_VAR_github_dispatch_token."
  type        = string
  sensitive   = true
}

variable "alarm_5xx_threshold" {
  description = "Target 5xx responses per minute that put a service alarm in ALARM."
  type        = number
  default     = 10
}

variable "alarm_p95_seconds" {
  description = "Target response time p95 (seconds) that puts a service alarm in ALARM."
  type        = number
  default     = 1.5
}
