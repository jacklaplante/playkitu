terraform {
  required_version = "= 1.16.6"
  required_providers {
    aws = { source = "hashicorp/aws", version = "= 6.69.0" }
  }
  backend "local" { path = "../private/foundation.tfstate" }
}
locals { c = module.configuration.contract }
provider "aws" {
  region              = local.c.region
  allowed_account_ids = [local.c.account]
  default_tags { tags = { Project = local.c.project, ManagedBy = "Terraform", Component = "recording" } }
}
# The pinned provider tags a newly created service-linked role when default
# tags exist. Keep this AWS-managed prerequisite untagged instead of granting
# seed TagRole authority. The alias retains the same account/region guards.
provider "aws" {
  alias               = "service_linked_role"
  region              = local.c.region
  allowed_account_ids = [local.c.account]
}
variable "create_api_gateway_service_linked_role" {
  type        = bool
  description = "Owner confirms presence/absence privately in IAM console before the first plan. True only if absent; never import/manage an unrelated existing shared role."
}
resource "aws_iam_service_linked_role" "api_gateway" {
  provider         = aws.service_linked_role
  count            = var.create_api_gateway_service_linked_role ? 1 : 0
  aws_service_name = "ops.apigateway.amazonaws.com"
  lifecycle { prevent_destroy = true }
}
resource "aws_apigatewayv2_api" "issuer" {
  name          = local.c.api
  protocol_type = "HTTP"
  # No quick-create target, route or stage. No Lambda exists/is invokable yet.
  depends_on = [aws_iam_service_linked_role.api_gateway]
  lifecycle { prevent_destroy = true }
}
output "api_id" { value = aws_apigatewayv2_api.issuer.id }

variable "configuration_file" {
  type        = string
  description = "Explicit owner configuration path; no default deployment or credentials."
}
module "configuration" {
  source             = "../modules/configuration"
  configuration_file = var.configuration_file
}
