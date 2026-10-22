mock_provider "aws" {
  # Model the default configuration's tags so accidentally rebinding the AWS
  # service-linked role to it fails the untagged-role assertion below.
  mock_resource "aws_iam_service_linked_role" {
    defaults = { tags_all = { Project = "playreel", ManagedBy = "Terraform", Component = "recording" } }
  }
}
mock_provider "aws" { alias = "service_linked_role" }
variables {
  configuration_file                     = "../deployment.example.json"
  create_api_gateway_service_linked_role = false
}
run "empty_named_api_only" {
  command = plan
  assert {
    condition     = aws_apigatewayv2_api.issuer.name == "playreel-api" && aws_apigatewayv2_api.issuer.protocol_type == "HTTP" && length(aws_iam_service_linked_role.api_gateway) == 0
    error_message = "Foundation must create only the empty named API by default."
  }
}
run "explicit_first_account_prerequisite" {
  command = apply
  variables { create_api_gateway_service_linked_role = true }
  assert {
    condition     = aws_iam_service_linked_role.api_gateway[0].aws_service_name == "ops.apigateway.amazonaws.com"
    error_message = "Only the API Gateway AWS-managed service-linked role may be created."
  }
  assert {
    condition     = length(aws_iam_service_linked_role.api_gateway[0].tags_all) == 0
    error_message = "The AWS-managed service-linked role must not inherit project tags."
  }
}

