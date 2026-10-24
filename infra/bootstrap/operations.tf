resource "aws_cloudwatch_log_group" "lambda" {
  name              = local.log_group
  retention_in_days = 30
}
resource "aws_cloudwatch_log_group" "access" {
  name              = "/${local.c.project}/api-access"
  retention_in_days = 30
}
resource "aws_apigatewayv2_stage" "issuer" {
  api_id      = var.api_id
  name        = "$default"
  auto_deploy = true
  # The owner enables account-level HTTP API log delivery through the console
  # after this closed stage exists. Reconcile the exact setting before invocation.
  # Bootstrap/deploy have no log resource-policy or log-delivery authority.
  dynamic "access_log_settings" {
    for_each = var.owner_logging_ready ? [1] : []
    content {
      destination_arn = aws_cloudwatch_log_group.access.arn
      format          = local.access_log_format
    }
  }
  default_route_settings {
    throttling_rate_limit    = local.c.api_rate
    throttling_burst_limit   = local.c.api_burst
    detailed_metrics_enabled = false
  }
  dynamic "route_settings" {
    for_each = var.runtime_ready ? [1] : []
    content {
      route_key                = "POST /v1/sessions"
      throttling_rate_limit    = local.c.stream_mode == "onDemand" ? local.c.recording_limits.admissionAttemptsPerMinute / 60 : 0.2
      throttling_burst_limit   = local.c.stream_mode == "onDemand" ? min(local.c.recording_limits.admissionAttemptsPerMinute, local.c.api_burst) : 2
      detailed_metrics_enabled = false
    }
  }
  lifecycle { prevent_destroy = true }
}
locals {
  # Preserve the owner's saved HTTP API access-log format byte for byte.
  # jsonencode sorts keys and creates an unnecessary format diff.
  access_log_format = "{\"requestId\":\"$context.requestId\",\"routeKey\":\"$context.routeKey\",\"status\":\"$context.status\",\"responseLatency\":\"$context.responseLatency\"}"
}
resource "aws_sns_topic" "alerts" { name = local.c.topic }
resource "aws_sns_topic_subscription" "email" {
  topic_arn = aws_sns_topic.alerts.arn
  protocol  = "email"
  endpoint  = local.c.alert_email
}
resource "aws_budgets_budget" "project" {
  name         = local.c.budget
  budget_type  = "COST"
  limit_amount = "100"
  limit_unit   = "USD"
  time_unit    = "MONTHLY"
  cost_filter {
    name   = "TagKeyValue"
    values = [join("$", ["user:Project", local.c.project])]
  }
  dynamic "notification" {
    for_each = { actual10 = { type = "ACTUAL", amount = 10 }, actual25 = { type = "ACTUAL", amount = 25 }, actual50 = { type = "ACTUAL", amount = 50 }, forecast75 = { type = "FORECASTED", amount = 75 } }
    content {
      comparison_operator        = "GREATER_THAN"
      threshold                  = notification.value.amount
      threshold_type             = "ABSOLUTE_VALUE"
      notification_type          = notification.value.type
      subscriber_email_addresses = [local.c.alert_email]
    }
  }
}
resource "aws_cloudwatch_metric_alarm" "lambda" {
  for_each            = toset(["Errors", "Throttles"])
  alarm_name          = "${local.c.project}-${lower(each.key)}"
  namespace           = "AWS/Lambda"
  metric_name         = each.key
  dimensions          = { FunctionName = local.c.function }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
  alarm_actions       = [aws_sns_topic.alerts.arn]
}
resource "aws_sns_topic_policy" "alerts" {
  arn = aws_sns_topic.alerts.arn
  policy = jsonencode({ Version = "2012-10-17", Statement = [
    { Sid = "CloudWatchAlarms", Effect = "Allow", Principal = { Service = "cloudwatch.amazonaws.com" }, Action = "sns:Publish", Resource = local.topic_arn, Condition = { StringEquals = { "aws:SourceAccount" = local.c.account }, ArnEquals = { "aws:SourceArn" = [for a in aws_cloudwatch_metric_alarm.lambda : a.arn] } } }
  ] })
}

resource "aws_cloudwatch_event_rule" "cleanup" {
  name                = local.c.schedule
  description         = "Reconcile expired recording leases and retained on-demand streams; no capture or admissions."
  schedule_expression = local.c.stream_mode == "onDemand" ? "rate(1 minute)" : "rate(1 day)"
  state               = var.runtime_ready ? "ENABLED" : "DISABLED"
}
resource "aws_cloudwatch_event_target" "cleanup" {
  count = var.runtime_ready ? 1 : 0
  rule  = aws_cloudwatch_event_rule.cleanup.name
  arn   = local.function_arn
  input = local.c.stream_mode == "onDemand" ? jsonencode({ version = "playkitu.cleanup.v1" }) : jsonencode({ version = "2.0", rawPath = "/v1/config", requestContext = { http = { method = "GET" } } })
  retry_policy {
    maximum_event_age_in_seconds = 3600
    maximum_retry_attempts       = 2
  }
  depends_on = [aws_lambda_permission.cleanup]
}
resource "aws_lambda_permission" "cleanup" {
  count          = var.runtime_ready ? 1 : 0
  statement_id   = "DailyConfigCleanup"
  action         = "lambda:InvokeFunction"
  function_name  = local.c.function
  principal      = "events.amazonaws.com"
  source_account = local.c.account
  source_arn     = aws_cloudwatch_event_rule.cleanup.arn
}
resource "aws_lambda_permission" "api" {
  for_each = var.runtime_ready ? {
    Config = "GET/v1/config", Start = "POST/v1/sessions", Authorize = "POST/v1/sessions/*/authorize", Close = "POST/v1/sessions/*/close"
  } : {}
  statement_id   = "Api${each.key}"
  action         = "lambda:InvokeFunction"
  function_name  = local.c.function
  principal      = "apigateway.amazonaws.com"
  source_account = local.c.account
  source_arn     = "arn:aws:execute-api:${local.c.region}:${local.c.account}:${var.api_id}/*/${each.value}"
}

output "runtime_inputs" {
  description = "Nonsecret handoff; save as private/runtime.auto.tfvars.json, never read bootstrap state from deploy."
  value = {
    api_id               = var.api_id
    api_endpoint         = "https://${var.api_id}.execute-api.${local.c.region}.amazonaws.com"
    issuer_role_arn      = local.roles.issuer
    media_role_arn       = local.roles.media
    publisher_secret_arn = aws_secretsmanager_secret.publisher.arn
    stream_arns          = local.stream_arns
  }
}
output "owner_inventory" {
  value = { state_bucket = local.c.state_bucket, recording_bucket = local.c.recording_bucket, table = local.c.table, alert_email = local.c.alert_email, roles = local.roles, boundary = local.boundary, cleanup_ready = var.runtime_ready, api_id = var.api_id, access_logging_ready = var.owner_logging_ready, access_log_destination = aws_cloudwatch_log_group.access.arn, access_log_format = local.access_log_format }
}
