mock_provider "aws" { override_during = plan }
variables {
  configuration_file     = "../deployment.example.json"
  expected_issuer_sha256 = filesha256("../../backend/issuer/private/issuer.zip")
  api_id                 = "abc123def4"
  api_endpoint           = "https://abc123def4.execute-api.us-east-2.amazonaws.com"
  issuer_role_arn        = "arn:aws:iam::123456789012:role/playkitu/playkitu-issuer"
  media_role_arn         = "arn:aws:iam::123456789012:role/playkitu/playkitu-media"
  publisher_secret_arn   = "arn:aws:secretsmanager:us-east-2:123456789012:secret:playkitu/publisher-key-AbCd12"
  stream_arns            = [for i in range(1, 5) : "arn:aws:kinesisvideo:us-east-2:123456789012:stream/playkitu-slot-0${i}/1700000000000"]
}
run "runtime_limits_and_protocol" {
  command = plan
  assert {
    condition     = aws_lambda_function.issuer.runtime == "dotnet10" && aws_lambda_function.issuer.memory_size == 256 && aws_lambda_function.issuer.timeout == 15 && aws_lambda_function.issuer.reserved_concurrent_executions == 2 && one(aws_lambda_function.issuer.architectures) == "x86_64" && aws_lambda_function.issuer.ephemeral_storage[0].size == 512
    error_message = "Reviewed runtime/resource/cost settings changed."
  }
  assert {
    condition     = jsondecode(aws_lambda_function.issuer.environment[0].variables.PLAYKITU_DEPLOYMENT).steamAppId == 123456 && jsondecode(aws_lambda_function.issuer.environment[0].variables.PLAYKITU_DEPLOYMENT).awsAccountId == "123456789012" && tolist(jsondecode(aws_lambda_function.issuer.environment[0].variables.PLAYKITU_DEPLOYMENT).streamArns) == var.stream_arns && !strcontains(aws_lambda_function.issuer.environment[0].variables.PLAYKITU_DEPLOYMENT, "secretValue") && length(aws_lambda_function.issuer.vpc_config) == 0
    error_message = "Owner trust and reference-only environment must match the selected deployment."
  }
  assert {
    condition     = length(aws_apigatewayv2_route.issuer) == 4 && aws_apigatewayv2_integration.issuer.payload_format_version == "2.0" && aws_apigatewayv2_integration.issuer.integration_type == "AWS_PROXY"
    error_message = "API protocol/routes/throttle limits regressed."
  }

}
run "on_demand_uses_configurable_limits_without_two_lambda_ceiling" {
  command = plan
  variables {
    configuration_file = "../deployment.on-demand.example.json"
    stream_arns        = []
  }
  assert {
    condition = (aws_lambda_function.issuer.reserved_concurrent_executions == -1 &&
      jsondecode(aws_lambda_function.issuer.environment[0].variables.PLAYKITU_DEPLOYMENT).streamMode == "onDemand" &&
      jsondecode(aws_lambda_function.issuer.environment[0].variables.PLAYKITU_DEPLOYMENT).recordingLimits.concurrentSessions == null &&
    length(jsondecode(aws_lambda_function.issuer.environment[0].variables.PLAYKITU_DEPLOYMENT).streamArns) == 0)
    error_message = "On-demand must carry explicit mode/limits and must not retain the old pool or two-execution ceiling."
  }
}
run "reject_other_media_role" {
  command = plan
  variables { media_role_arn = "arn:aws:iam::123456789012:role/Administrator" }
  expect_failures = [var.media_role_arn]
}
run "reject_stream_count" {
  command = plan
  variables { stream_arns = [] }
  expect_failures = [var.stream_arns]
}
run "reject_other_account_secret" {
  command = plan
  variables { publisher_secret_arn = "arn:aws:secretsmanager:us-east-2:111111111111:secret:other-AbCd12" }
  expect_failures = [var.publisher_secret_arn]
}

run "reject_cross_region_api" {
  command = plan
  variables { api_endpoint = "https://abc123def4.execute-api.eu-west-1.amazonaws.com" }
  expect_failures = [var.api_endpoint]
}

