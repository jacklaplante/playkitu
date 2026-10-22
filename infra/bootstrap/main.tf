terraform {
  required_version = "= 1.16.6"
  required_providers {
    aws = { source = "hashicorp/aws", version = "= 6.69.0" }
  }
  # First apply uses private local state. An ignored backend_override.tf replaces
  # this backend only after the owner verifies the new state bucket protections.
  backend "local" { path = "../private/bootstrap.tfstate" }
}

provider "aws" {
  region              = local.c.region
  allowed_account_ids = [local.c.account]
  default_tags { tags = local.tags }
}

locals {
  c            = module.configuration.contract
  tags         = { Project = local.c.project, ManagedBy = "Terraform", Component = "recording" }
  roles        = { for name in local.c.roles : name => "arn:aws:iam::${local.c.account}:role${local.c.role_path}${local.c.project}-${name}" }
  boundary     = "arn:aws:iam::${local.c.account}:policy/${local.c.boundary_name}"
  table_arn    = "arn:aws:dynamodb:${local.c.region}:${local.c.account}:table/${local.c.table}"
  log_group    = "/aws/lambda/${local.c.function}"
  log_arn      = "arn:aws:logs:${local.c.region}:${local.c.account}:log-group:${local.log_group}"
  function_arn = "arn:aws:lambda:${local.c.region}:${local.c.account}:function:${local.c.function}"
  topic_arn    = "arn:aws:sns:${local.c.region}:${local.c.account}:${local.c.topic}"
  # IAM users authenticate with MFA before console Switch Role. Federated users
  # must use their existing MFA-enforced federation; its exact role is still pinned.
  operator_condition = can(regex(":user/", var.operator_principal_arn)) ? { Bool = { "aws:MultiFactorAuthPresent" = "true" } } : {}
  operator_trust     = jsonencode({ Version = "2012-10-17", Statement = [{ Effect = "Allow", Principal = { AWS = var.operator_principal_arn }, Action = "sts:AssumeRole", Condition = local.operator_condition }] })
}

variable "operator_principal_arn" {
  type        = string
  description = "Owner-confirmed existing non-root IAM user or federation role ARN; never a guessed identity."
  validation {
    condition     = can(regex("^arn:aws:iam::${local.c.account}:(user|role)/[A-Za-z0-9_+=,.@/-]+$", var.operator_principal_arn)) && !startswith(var.operator_principal_arn, "arn:aws:iam::${local.c.account}:role${local.c.role_path}") && var.operator_principal_arn != "arn:aws:iam::${local.c.account}:role/${local.c.seed_role}"
    error_message = "Supply the confirmed exact non-root principal in the configured account."
  }
}
variable "api_id" {
  type        = string
  description = "Actual API ID from the separately reviewed foundation apply; no remote-state access."
  validation {
    condition     = can(regex("^[a-z0-9]{10}$", var.api_id))
    error_message = "Supply the actual Terraform foundation API ID; no guessed ID."
  }
}

variable "runtime_ready" {
  type        = bool
  default     = false
  description = "Second owner checkpoint only: Lambda exists, disabled CONTROL seeded; attach invoke permissions and enable daily cleanup."
  validation {
    condition     = !var.runtime_ready || var.owner_logging_ready
    error_message = "Owner must enable/reconcile HTTP API access logging before invocation."
  }

}

variable "owner_logging_ready" {
  type        = bool
  default     = false
  description = "Owner enabled the exact stage access logs in the console and reviewed its account-level log-delivery changes; persist true after reconciliation."
}

variable "configuration_file" {
  type        = string
  description = "Explicit owner configuration path; no default deployment or credentials."
}
module "configuration" {
  source             = "../modules/configuration"
  configuration_file = var.configuration_file
}
