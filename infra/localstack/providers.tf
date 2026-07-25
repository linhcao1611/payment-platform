terraform {
  required_version = ">= 1.5"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    docker = {
      source  = "kreuzwerker/docker"
      version = "~> 3.0"
    }
  }
}

# Every endpoint is pinned at LocalStack and the credentials are dummies.
#
# This is a safety property, not a convenience. This machine has real AWS credentials in
# ~/.aws, and without these overrides a stray apply would reach a real account. It is also why
# `tflocal` is not required: the safety lives in the configuration, where it can be reviewed
# and version-controlled, rather than in how someone happened to invoke Terraform.
provider "aws" {
  region                      = var.aws_region
  access_key                  = "test"
  secret_key                  = "test"
  s3_use_path_style           = true
  skip_credentials_validation = true
  skip_metadata_api_check     = true
  skip_requesting_account_id  = true

  default_tags {
    tags = {
      Project   = var.project
      ManagedBy = "terraform"
    }
  }

  endpoints {
    apigateway     = var.localstack_endpoint
    cloudwatch     = var.localstack_endpoint
    ec2            = var.localstack_endpoint
    iam            = var.localstack_endpoint
    logs           = var.localstack_endpoint
    s3             = var.localstack_endpoint
    secretsmanager = var.localstack_endpoint
    sqs            = var.localstack_endpoint
    ssm            = var.localstack_endpoint
    sts            = var.localstack_endpoint
  }
}

provider "docker" {}
