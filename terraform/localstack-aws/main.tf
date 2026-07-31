terraform {
  required_version = ">= 1.14.0"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }

  backend "local" {
    path = "terraform.tfstate.localstack"
  }
}

provider "aws" {
  access_key                  = "test"
  secret_key                  = "test"
  region                      = "us-west-1"
  s3_use_path_style           = true
  skip_credentials_validation = true
  skip_metadata_api_check     = true
  skip_requesting_account_id  = false

  endpoints {
    sqs            = "http://localhost:4566"
    iam            = "http://localhost:4566"
    sts            = "http://localhost:4566"
    secretsmanager = "http://localhost:4566"
    s3             = "http://localhost:4566"
    ecs            = "http://localhost:4566"
    rds            = "http://localhost:4566"
    logs           = "http://localhost:4566"
    ec2            = "http://localhost:4566"
  }
}
