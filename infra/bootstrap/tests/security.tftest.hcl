mock_provider "aws" {
  mock_resource "aws_cloudwatch_event_rule" {
    defaults = { arn = "arn:aws:events:us-east-2:123456789012:rule/playkitu-daily-cleanup" }
  }

  mock_resource "aws_iam_role" {
    defaults = { arn = "arn:aws:iam::123456789012:role/playkitu/playkitu-issuer" }
  }
  mock_resource "aws_apigatewayv2_api" {
    defaults = {
      id            = "abc123def4"
      api_endpoint  = "https://abc123def4.execute-api.us-east-2.amazonaws.com"
      execution_arn = "arn:aws:execute-api:us-east-2:123456789012:abc123def4"
    }
  }
  mock_resource "aws_cloudwatch_log_group" {
    defaults = { arn = "arn:aws:logs:us-east-2:123456789012:log-group:/playkitu/api-access" }
  }
  mock_resource "aws_sns_topic" {
    defaults = { arn = "arn:aws:sns:us-east-2:123456789012:playkitu-alerts" }
  }
}
variables {
  configuration_file     = "../deployment.example.json"
  operator_principal_arn = "arn:aws:iam::123456789012:user/offline-fixture"
  api_id                 = "abc123def4"
}

run "on_demand_has_no_pool_and_only_scoped_stream_management" {
  command = apply
  variables {
    configuration_file  = "../deployment.on-demand.example.json"
    runtime_ready       = true
    owner_logging_ready = true
  }
  assert {
    condition = length(aws_kinesis_video_stream.slot) == 0 && anytrue([
      for index in aws_dynamodb_table.sessions.global_secondary_index : index.name == "stream-work-by-due" && index.projection_type == "ALL"
    ])
    error_message = "On-demand uses a durable due-work index, not a precreated pool."
  }
  assert {
    condition     = aws_cloudwatch_event_rule.cleanup.schedule_expression == "rate(1 minute)" && jsondecode(aws_cloudwatch_event_target.cleanup[0].input).version == "playkitu.cleanup.v1"
    error_message = "On-demand cleanup must use the private scheduled event every minute."
  }
  assert {
    condition = anytrue([for statement in jsondecode(aws_iam_role_policy.project["issuer"].policy).Statement :
      !contains(statement.Action, "kinesisvideo:TagStream") ? false :
      (statement.Resource == "arn:aws:kinesisvideo:us-east-2:123456789012:stream/playkitu-slot-session-$${aws:RequestTag/Admission}/*" && statement.Condition.StringEquals["aws:RequestTag/Project"] == "playkitu" && statement.Condition.StringEquals["aws:RequestTag/ManagedBy"] == "playkitu")
    ]) && alltrue([for name in ["media", "retrieval", "deploy", "operations"] : !strcontains(aws_iam_role_policy.project[name].policy, "kinesisvideo:TagStream")])
    error_message = "Tag-on-create must bind the Admission tag to the exact stream name; only issuer may tag."
  }
  assert {
    condition = alltrue([for statement in jsondecode(aws_iam_role_policy.project["issuer"].policy).Statement :
      !contains(statement.Action, "kinesisvideo:CreateStream") ? true :
      (statement.Resource == local.session_streams && statement.Condition.StringEquals["aws:RequestTag/Project"] == "playkitu" && statement.Condition.StringEquals["aws:RequestTag/ManagedBy"] == "playkitu")
    ]) && anytrue([for statement in jsondecode(aws_iam_role_policy.project["issuer"].policy).Statement : contains(statement.Action, "kinesisvideo:CreateStream")])
    error_message = "Creation must be restricted to admission names and required ownership tags."
  }
  assert {
    condition = alltrue([for statement in jsondecode(aws_iam_role_policy.project["issuer"].policy).Statement :
      !contains(statement.Action, "kinesisvideo:DeleteStream") ? true :
      (statement.Resource == local.session_streams && statement.Condition.StringEquals["aws:ResourceTag/Project"] == "playkitu" && statement.Condition.StringEquals["aws:ResourceTag/ManagedBy"] == "playkitu")
    ]) && alltrue([for name in ["media", "retrieval", "deploy", "operations"] : !strcontains(aws_iam_role_policy.project[name].policy, "kinesisvideo:DeleteStream") && !strcontains(aws_iam_role_policy.project[name].policy, "kinesisvideo:CreateStream")])
    error_message = "Only the issuer manages tagged session streams; clients and human retrieval roles do not."
  }
}

run "owner_storage_and_boundaries" {
  command = apply
  assert {
    condition     = aws_iam_role.issuer.max_session_duration == 3600 && aws_iam_role.project["media"].max_session_duration == 3600 && toset(keys(aws_iam_role.project)) == toset(["media", "deploy", "operations", "retrieval"]) && alltrue([for name in ["deploy", "operations", "retrieval"] : aws_iam_role.project[name].max_session_duration == 43200])
    error_message = "Only deploy/operations/retrieval may have twelve-hour sessions; issuer/media must remain one hour."
  }
  assert {
    condition     = length(aws_kinesis_video_stream.slot) == 4 && alltrue([for s in aws_kinesis_video_stream.slot : s.data_retention_in_hours == 720])
    error_message = "Exactly four retained streams must remain at 720 hours."
  }
  assert {
    condition     = aws_s3_bucket_versioning.state.versioning_configuration[0].status == "Enabled" && aws_s3_bucket_versioning.recording.versioning_configuration[0].status == "Suspended" && !aws_s3_bucket.recording.force_destroy
    error_message = "State needs history; recording versions/forced deletion must stay off."
  }
  assert {
    condition     = aws_s3_bucket_public_access_block.state.block_public_policy && aws_s3_bucket_public_access_block.recording.block_public_acls && aws_s3_bucket_public_access_block.recording.ignore_public_acls && aws_s3_bucket_public_access_block.recording.restrict_public_buckets && aws_s3_bucket_public_access_block.recording.block_public_policy
    error_message = "Private state/recording public-access protections regressed."
  }
  assert {
    condition     = one(aws_s3_bucket_lifecycle_configuration.recording.rule).expiration[0].days == 30 && aws_dynamodb_table.sessions.ttl[0].attribute_name == "purgeAt" && aws_dynamodb_table.sessions.ttl[0].enabled && aws_dynamodb_table.sessions.deletion_protection_enabled && one(aws_dynamodb_table.sessions.global_secondary_index).projection_type == "KEYS_ONLY"
    error_message = "Expiry, table protection or sparse index changed."
  }
  assert {
    condition     = aws_iam_role.issuer.permissions_boundary == local.boundary && alltrue([for r in aws_iam_role.project : r.permissions_boundary == local.boundary]) && jsondecode(aws_iam_role.project["media"].assume_role_policy).Statement[0].Principal.AWS == aws_iam_role.issuer.arn
    error_message = "Every managed role requires the console-owned ceiling; only issuer assumes media."
  }
  assert {
    condition     = alltrue([for name in ["deploy", "operations", "retrieval"] : length(jsondecode(aws_iam_role.project[name].assume_role_policy).Statement) == 1 && jsondecode(aws_iam_role.project[name].assume_role_policy).Statement[0].Principal.AWS == var.operator_principal_arn && jsondecode(aws_iam_role.project[name].assume_role_policy).Statement[0].Action == "sts:AssumeRole" && jsondecode(aws_iam_role.project[name].assume_role_policy).Statement[0].Condition.Bool["aws:MultiFactorAuthPresent"] == "true"])
    error_message = "Operator trust/MFA broadened."
  }
  assert {
    condition     = jsondecode(aws_iam_role_policy.project["issuer"].policy).Statement[0].Action == ["dynamodb:GetItem"] && jsondecode(aws_iam_role_policy.project["issuer"].policy).Statement[1].Action == ["dynamodb:PutItem"] && jsondecode(aws_iam_role_policy.project["issuer"].policy).Statement[1].Condition["ForAnyValue:StringEquals"]["dynamodb:EnclosingOperation"] == ["TransactWriteItems"]
    error_message = "Runtime must use independent GetItem and transaction-restricted PutItem."
  }
  assert {
    condition     = alltrue([for s in jsondecode(aws_iam_role_policy.project["deploy"].policy).Statement : alltrue([for a in s.Action : (!startswith(a, "iam:") || a == "iam:PassRole") && a != "lambda:DeleteFunction"])]) && !strcontains(aws_iam_role_policy.project["deploy"].policy, "secretsmanager:") && !strcontains(aws_iam_role_policy.project["deploy"].policy, "lambda:AddPermission")
    error_message = "Routine deploy authority must not expand permissions/read secrets/delete retained services."
  }
  assert {
    condition = alltrue([for s in jsondecode(aws_iam_role_policy.project["deploy"].policy).Statement :
      !contains(s.Action, "lambda:DeleteFunctionConcurrency") ? true : s.Resource == local.function_arn
    ]) && anytrue([for s in jsondecode(aws_iam_role_policy.project["deploy"].policy).Statement : contains(s.Action, "lambda:DeleteFunctionConcurrency")])
    error_message = "Removing reserved concurrency must work for this function only, without permission to delete the service."
  }
  assert {
    condition     = length(aws_lambda_permission.api) == 0 && length(aws_lambda_permission.cleanup) == 0 && aws_cloudwatch_event_rule.cleanup.state == "DISABLED" && length(aws_cloudwatch_event_target.cleanup) == 0
    error_message = "First bootstrap must not activate invocation before runtime exists."
  }
  assert {
    condition     = aws_cloudwatch_log_group.lambda.retention_in_days == 30 && aws_cloudwatch_log_group.access.retention_in_days == 30 && aws_sns_topic_subscription.email.endpoint == "alerts@example.invalid" && length(aws_cloudwatch_metric_alarm.lambda) == 2 && length(aws_budgets_budget.project.notification) == 4 && one(one(aws_budgets_budget.project.cost_filter).values) == "user:Project$playkitu"
    error_message = "Operational retention/alert destination/budget filter changed."
  }
  assert {
    condition     = alltrue([for s in jsondecode(aws_iam_role_policy.project["media"].policy).Statement : toset(s.Action) == toset(["kinesisvideo:DescribeStream", "kinesisvideo:GetDataEndpoint", "kinesisvideo:PutMedia"]) && length(s.Resource) == 4])
    error_message = "Player media role must not acquire reads/configuration/resource creation."
  }
}

run "configured_eight_stream_pool" {
  command = plan
  variables {
    configuration_file = "../private/tests/deployment-eight.json"
  }
  assert {
    condition     = length(aws_kinesis_video_stream.slot) == 8 && aws_kinesis_video_stream.slot["08"].name == "playkitu-slot-08"
    error_message = "videoStreamCount must control the entire numbered stream pool."
  }
}
run "readiness_delta" {
  command = plan
  variables {
    runtime_ready       = true
    owner_logging_ready = true
  }
}
run "reject_readiness_without_owner_logging" {
  command = plan
  variables {
    runtime_ready       = true
    owner_logging_ready = false
  }
  expect_failures = [var.runtime_ready]
}
run "owner_second_checkpoint" {
  command = apply
  variables {
    runtime_ready       = true
    owner_logging_ready = true
  }
  assert {
    condition     = length(aws_lambda_permission.api) == 4 && aws_lambda_permission.cleanup[0].source_account == "123456789012" && aws_cloudwatch_event_rule.cleanup.state == "ENABLED" && aws_cloudwatch_event_rule.cleanup.schedule_expression == "rate(1 day)" && jsondecode(aws_cloudwatch_event_target.cleanup[0].input).rawPath == "/v1/config" && aws_cloudwatch_event_target.cleanup[0].retry_policy[0].maximum_retry_attempts == 2
    error_message = "Cleanup must invoke only the exact config path on the daily schedule."
  }
  assert {
    condition     = alltrue([for p in aws_lambda_permission.api : p.source_account == "123456789012" && startswith(p.source_arn, "arn:aws:execute-api:us-east-2:123456789012:abc123def4/*/")])
    error_message = "API resource permissions must pin account/API/routes."
  }
  assert {
    condition     = aws_apigatewayv2_stage.issuer.default_route_settings[0].throttling_rate_limit == 3 && one(aws_apigatewayv2_stage.issuer.route_settings).throttling_rate_limit == 0.2 && toset(keys(jsondecode(aws_apigatewayv2_stage.issuer.access_log_settings[0].format))) == toset(["requestId", "routeKey", "status", "responseLatency"])
    error_message = "Owner-managed access logs/throttles changed."
  }
}
run "reject_unknown_foundation_api" {
  command = plan
  variables { api_id = "unknown" }
  expect_failures = [var.api_id]
}
run "reject_root_operator" {
  command = plan
  variables { operator_principal_arn = "arn:aws:iam::123456789012:root" }
  expect_failures = [var.operator_principal_arn]
}

run "reject_other_account_operator" {
  command = plan
  variables { operator_principal_arn = "arn:aws:iam::210987654321:user/offline-fixture" }
  expect_failures = [var.operator_principal_arn]
}
