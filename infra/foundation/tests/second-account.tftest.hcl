mock_provider "aws" {}
mock_provider "aws" { alias = "service_linked_role" }
variables {
  configuration_file                     = "../private/tests/deployment-second.json"
  create_api_gateway_service_linked_role = false
}
run "independent_second_account_region" {
  command = plan
  variables { configuration_file = "../private/tests/deployment-second.json" }
  assert {
    condition     = local.c.account == "210987654321" && local.c.region == "eu-west-1" && local.c.steam_app_id == 654321
    error_message = "Owner trust must be configurable without source edits."
  }
}



