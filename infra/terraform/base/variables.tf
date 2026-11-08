variable "region" {
  type    = string
  default = "us-east-1"
}

variable "github_repo" {
  description = "owner/name of the GitHub repository whose workflows may assume the roles."
  type        = string
  default     = "billigmeier-labs/orders-demo"
}

variable "zone_id" {
  description = "Route 53 public hosted zone for joshb.app."
  type        = string
  default     = "Z04795242Y2F7953D3FBP"
}

variable "domain" {
  description = "Apex of the demo hosts."
  type        = string
  default     = "demo.joshb.app"
}
