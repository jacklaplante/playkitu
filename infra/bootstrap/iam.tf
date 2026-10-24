resource "aws_iam_role" "issuer" {
  name                 = "${local.c.project}-issuer"
  path                 = local.c.role_path
  max_session_duration = 3600
  permissions_boundary = local.boundary
  assume_role_policy   = jsonencode({ Version = "2012-10-17", Statement = [{ Effect = "Allow", Principal = { Service = "lambda.amazonaws.com" }, Action = "sts:AssumeRole" }] })
}
resource "aws_iam_role" "project" {
  for_each = setsubtract(toset(local.c.roles), toset(["issuer"]))
  name     = "${local.c.project}-${each.key}"
  path     = local.c.role_path
  # Only routine human roles get longer renewable CLI sessions. Media keeps its
  # short ceiling; the expired console-owned bootstrap is outside this resource.
  max_session_duration = contains(["deploy", "operations", "retrieval"], each.key) ? 43200 : 3600
  permissions_boundary = local.boundary
  assume_role_policy   = each.key == "media" ? jsonencode({ Version = "2012-10-17", Statement = [{ Effect = "Allow", Principal = { AWS = aws_iam_role.issuer.arn }, Action = "sts:AssumeRole" }] }) : local.operator_trust
}

locals {
  stream_arns      = [for k in sort(keys(aws_kinesis_video_stream.slot)) : aws_kinesis_video_stream.slot[k].arn]
  session_streams  = "arn:aws:kinesisvideo:${local.c.region}:${local.c.account}:stream/${local.c.stream_name_prefix}session-${join("", [for i in range(32) : "?"])}/*"
  readable_streams = concat(local.stream_arns, local.c.stream_mode == "onDemand" ? [local.session_streams] : [])
  on_demand_issuer_statements = [for statement in [
    { Sid = "ControlFenceOnly", Effect = "Allow", Action = ["dynamodb:ConditionCheckItem"], Resource = local.table_arn, Condition = { "ForAllValues:StringEquals" = { "dynamodb:LeadingKeys" = ["CONTROL"] } } },
    { Sid = "DueStreamWork", Effect = "Allow", Action = ["dynamodb:Query"], Resource = "${local.table_arn}/index/stream-work-by-due" },
    { Sid = "ExactAdmissionMetadata", Effect = "Allow", Action = ["kinesisvideo:DescribeStream", "kinesisvideo:ListTagsForStream"], Resource = local.session_streams },
    { Sid = "CreateTaggedAdmissionStream", Effect = "Allow", Action = ["kinesisvideo:CreateStream"], Resource = local.session_streams, Condition = {
      StringEquals                = { "aws:RequestTag/Project" = local.c.project, "aws:RequestTag/ManagedBy" = "playkitu" },
      StringLike                  = { "aws:RequestTag/Admission" = join("", [for i in range(32) : "?"]) },
      "ForAllValues:StringEquals" = { "aws:TagKeys" = ["Project", "ManagedBy", "Admission"] }
    } },
    { Sid = "RetireOwnedAdmissionStream", Effect = "Allow", Action = ["kinesisvideo:DeleteStream"], Resource = local.session_streams, Condition = {
      StringEquals = { "aws:ResourceTag/Project" = local.c.project, "aws:ResourceTag/ManagedBy" = "playkitu" },
      StringLike   = { "aws:ResourceTag/Admission" = join("", [for i in range(32) : "?"]) }
    } }
  ] : statement if local.c.stream_mode == "onDemand"]
  signin = { Effect = "Allow", Action = ["signin:AuthorizeOAuth2Access", "signin:CreateOAuth2Token"], Resource = "arn:aws:signin:${local.c.region}:${local.c.account}:oauth2/public-client/localhost" }
  issuer_statements = concat([
    { Sid = "StrongReads", Effect = "Allow", Action = ["dynamodb:GetItem"], Resource = local.table_arn },
    # TransactWriteItems is an API, not an IAM action. Do not grant ordinary PutItem.
    { Sid = "AtomicPutsOnly", Effect = "Allow", Action = ["dynamodb:PutItem"], Resource = local.table_arn, Condition = { "ForAnyValue:StringEquals" = { "dynamodb:EnclosingOperation" = ["TransactWriteItems"] } } },
    { Effect = "Allow", Action = ["secretsmanager:GetSecretValue"], Resource = aws_secretsmanager_secret.publisher.arn },
    { Effect = "Allow", Action = ["sts:AssumeRole"], Resource = local.roles.media },
    { Effect = "Allow", Action = ["s3:PutObject"], Resource = "${aws_s3_bucket.recording.arn}/sessions/*" },
    { Effect = "Allow", Action = ["logs:CreateLogStream", "logs:PutLogEvents"], Resource = "${local.log_arn}:log-stream:*" }
  ], local.on_demand_issuer_statements)
  media_statements = [{ Effect = "Allow", Action = ["kinesisvideo:DescribeStream", "kinesisvideo:GetDataEndpoint", "kinesisvideo:PutMedia"], Resource = local.readable_streams }]
  deploy_statements = [
    local.signin,
    { Sid = "FunctionOnly", Effect = "Allow", Action = ["lambda:CreateFunction", "lambda:GetFunction", "lambda:GetFunctionConfiguration", "lambda:GetFunctionCodeSigningConfig", "lambda:UpdateFunctionCode", "lambda:UpdateFunctionConfiguration", "lambda:PutFunctionConcurrency", "lambda:GetFunctionConcurrency", "lambda:ListTags", "lambda:TagResource", "lambda:UntagResource", "lambda:ListVersionsByFunction", "lambda:GetRuntimeManagementConfig"], Resource = local.function_arn },
    { Sid = "PassIssuerOnly", Effect = "Allow", Action = ["iam:PassRole"], Resource = local.roles.issuer, Condition = { StringEquals = { "iam:PassedToService" = "lambda.amazonaws.com" } } },
    # Foundation creates the API shell so deploy never needs POST /apis or access to other APIs.
    { Sid = "ExistingApiRead", Effect = "Allow", Action = ["apigateway:GET"], Resource = "arn:aws:apigateway:${local.c.region}::/apis/${var.api_id}" },
    { Sid = "ApiChildren", Effect = "Allow", Action = ["apigateway:GET", "apigateway:POST", "apigateway:PATCH", "apigateway:DELETE"], Resource = [for child in ["routes", "integrations"] : "arn:aws:apigateway:${local.c.region}::/apis/${var.api_id}/${child}*"] },
    { Sid = "StateList", Effect = "Allow", Action = ["s3:ListBucket"], Resource = aws_s3_bucket.state.arn, Condition = { StringLike = { "s3:prefix" = ["runtime/terraform.tfstate", "runtime/terraform.tfstate.tflock", "runtime/workspaces-disabled/"] } } },
    { Sid = "RuntimeState", Effect = "Allow", Action = ["s3:GetObject", "s3:PutObject"], Resource = "${aws_s3_bucket.state.arn}/runtime/terraform.tfstate" },
    { Sid = "RuntimeLock", Effect = "Allow", Action = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"], Resource = "${aws_s3_bucket.state.arn}/runtime/terraform.tfstate.tflock" }
  ]
  retrieval_statements = [
    local.signin,
    { Effect = "Allow", Action = ["dynamodb:GetItem"], Resource = local.table_arn, Condition = { "ForAllValues:StringLike" = { "dynamodb:LeadingKeys" = ["SESSION#*"] } } },
    { Effect = "Allow", Action = ["dynamodb:Query"], Resource = "${local.table_arn}/index/sessions-by-day" },
    { Effect = "Allow", Action = ["s3:ListBucket"], Resource = aws_s3_bucket.recording.arn, Condition = { StringLike = { "s3:prefix" = ["sessions/*"] } } },
    { Effect = "Allow", Action = ["s3:GetObject"], Resource = "${aws_s3_bucket.recording.arn}/sessions/*" },
    { Effect = "Allow", Action = ["kinesisvideo:DescribeStream", "kinesisvideo:GetDataEndpoint", "kinesisvideo:ListFragments", "kinesisvideo:GetMediaForFragmentList", "kinesisvideo:GetClip"], Resource = local.readable_streams }
  ]
  operations_statements = [
    local.signin,
    { Effect = "Allow", Action = ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem"], Resource = local.table_arn, Condition = { "ForAllValues:StringEquals" = { "dynamodb:LeadingKeys" = ["CONTROL"] } } }
  ]
  policies = {
    issuer     = jsonencode({ Version = "2012-10-17", Statement = local.issuer_statements })
    media      = jsonencode({ Version = "2012-10-17", Statement = local.media_statements })
    deploy     = jsonencode({ Version = "2012-10-17", Statement = local.deploy_statements })
    retrieval  = jsonencode({ Version = "2012-10-17", Statement = local.retrieval_statements })
    operations = jsonencode({ Version = "2012-10-17", Statement = local.operations_statements })
  }
}
resource "aws_iam_role_policy" "project" {
  for_each = local.policies
  name     = "recording-permissions"
  role     = each.key == "issuer" ? aws_iam_role.issuer.id : aws_iam_role.project[each.key].id
  policy   = each.value
}
