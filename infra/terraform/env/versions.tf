terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  backend "s3" {
    bucket       = "joshb-terraform-state-983889169993"
    key          = "orders-demo/env.tfstate"
    region       = "us-east-1"
    encrypt      = true
    use_lockfile = true
  }
}

provider "aws" {
  region = var.region

  default_tags {
    tags = {
      project = "orders-demo"
      # The demo run this environment belongs to (SSM /orders-demo/demo-run, set by `just env up`), so
      # leftovers and costs can be traced to one demo. `just env down` fails if anything with it remains.
      demo-run = var.demo_run
    }
  }
}

data "aws_caller_identity" "current" {}
