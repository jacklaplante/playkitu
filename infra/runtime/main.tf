terraform {
  required_version = "= 1.16.6"
  required_providers {
    aws = { source = "hashicorp/aws", version = "= 6.69.0" }
  }
  # Initialized only after owner bootstrap/migration approval. Offline checks use -backend=false.
  backend "s3" {}
}
locals {
  c      = module.configuration.contract
  routes = toset(["GET /v1/config", "POST /v1/sessions", "POST /v1/sessions/{id}/authorize", "POST /v1/sessions/{id}/close"])
}
provider "aws" {
  region              = local.c.region
  allowed_account_ids = [local.c.account]
  default_tags { tags = { Project = local.c.project, ManagedBy = "Terraform", Component = "recording" } }
}

variable "api_id" {
  type = string
  validation {
    condition     = can(regex("^[a-z0-9]{10}$", var.api_id))
    error_message = "Use the actual Terraform foundation API ID."
  }
}
variable "api_endpoint" {
  type = string
  validation {
    condition     = var.api_endpoint == "https://${var.api_id}.execute-api.${local.c.region}.amazonaws.com"
    error_message = "Endpoint must be the owner-created configured regional API HTTPS host."
  }
}
variable "issuer_role_arn" {
  type = string
  validation {
    condition     = var.issuer_role_arn == "arn:aws:iam::${local.c.account}:role${local.c.role_path}${local.c.project}-issuer"
    error_message = "Only the dedicated issuer role can be passed to Lambda."
  }
}
variable "media_role_arn" {
  type = string
  validation {
    condition     = var.media_role_arn == "arn:aws:iam::${local.c.account}:role${local.c.role_path}${local.c.project}-media"
    error_message = "Use the dedicated media role."
  }
}
variable "publisher_secret_arn" {
  type = string
  validation {
    condition     = can(regex("^arn:aws:secretsmanager:${local.c.region}:${local.c.account}:secret:${local.c.project}/publisher-key-[A-Za-z0-9]{6}$", var.publisher_secret_arn))
    error_message = "Use metadata ARN of the new owner-managed secret; never its value."
  }
}
variable "stream_arns" {
  type = list(string)
  validation {
    condition = length(var.stream_arns) == 4 && length(toset(var.stream_arns)) == 4 && alltrue([
      for i, arn in var.stream_arns : can(regex("^arn:aws:kinesisvideo:${local.c.region}:${local.c.account}:stream/${local.c.stream_name_prefix}0${i + 1}/[0-9]+$", arn))
    ])
    error_message = "Use four distinct actual slot stream ARNs, ordered 01 through 04."
  }
}
variable "issuer_zip" {
  type        = string
  default     = "../../backend/issuer/private/issuer.zip"
  description = "Relative to runtime root or absolute; reviewed issuer ZIP must match the explicit expected SHA256."
  validation {
    condition     = fileexists(var.issuer_zip) && try(filesha256(var.issuer_zip), "") == var.expected_issuer_sha256
    error_message = "Build/review the new issuer ZIP; explicit SHA256 must match."
  }
}

resource "aws_lambda_function" "issuer" {
  function_name                  = local.c.function
  role                           = var.issuer_role_arn
  runtime                        = "dotnet10"
  architectures                  = ["x86_64"]
  handler                        = "Playkitu.Issuer::Playkitu.Issuer.Function::HandleAsync"
  filename                       = var.issuer_zip
  source_code_hash               = filebase64sha256(var.issuer_zip)
  memory_size                    = 256
  timeout                        = 15
  reserved_concurrent_executions = 2
  ephemeral_storage { size = 512 }
  environment {
    variables = {
      PLAYKITU_DEPLOYMENT = jsonencode({
        awsAccountId       = local.c.account
        awsRegion          = local.c.region
        steamAppId         = local.c.steam_app_id
        issuerOrigin       = var.api_endpoint
        project            = local.c.project
        rolePath           = local.c.role_path
        streamNamePrefix   = local.c.stream_name_prefix
        table              = local.c.table
        uploadBucket       = local.c.recording_bucket
        publisherSecretArn = var.publisher_secret_arn
        mediaRoleArn       = var.media_role_arn
        retrievalRoleArn   = "arn:aws:iam::${local.c.account}:role${local.c.role_path}${local.c.project}-retrieval"
        streamArns         = var.stream_arns
        identityPrefix     = local.c.identity_prefix
        identityDomain     = local.c.identity_domain
      })
    }
  }

  # No secret values, VPC/NAT, function URL, dead-letter payload capture or async destinations.
  lifecycle { prevent_destroy = true }
}
resource "aws_apigatewayv2_integration" "issuer" {
  api_id                 = var.api_id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.issuer.invoke_arn
  integration_method     = "POST"
  payload_format_version = "2.0"
  timeout_milliseconds   = 16000
}
resource "aws_apigatewayv2_route" "issuer" {
  for_each           = local.routes
  api_id             = var.api_id
  route_key          = each.key
  authorization_type = "NONE"
  target             = "integrations/${aws_apigatewayv2_integration.issuer.id}"
}
output "issuer_endpoint" { value = var.api_endpoint }
output "function_arn" { value = aws_lambda_function.issuer.arn }

variable "configuration_file" {
  type        = string
  description = "Explicit owner configuration path; no default deployment or credentials."
}
module "configuration" {
  source             = "../modules/configuration"
  configuration_file = var.configuration_file
}

variable "expected_issuer_sha256" {
  type        = string
  description = "Reviewed newly built ZIP hash, never a historical deployment hash."
  validation {
    condition     = can(regex("^[a-fA-F0-9]{64}$", var.expected_issuer_sha256))
    error_message = "Explicit reviewed SHA256 required."
  }
}
output "download_configuration" { value = jsondecode(aws_lambda_function.issuer.environment[0].variables.PLAYKITU_DEPLOYMENT) }
