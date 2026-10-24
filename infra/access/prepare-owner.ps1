param(
    [Parameter(Mandatory)][string]$ConfigurationFile,
    [Parameter(Mandatory)][string]$OperatorPrincipalArn,
    [ValidatePattern('^[a-z0-9]{10}$')][string]$ApiId,
    [Parameter(Mandatory)][DateTimeOffset]$WindowStartUtc,
    [Parameter(Mandatory)][DateTimeOffset]$WindowEndUtc,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputDirectory,
    [switch]$ExpiredExample
)
# Render reviewable console policies locally. Never sign in or execute AWS commands.
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot
. (Join-Path $taskRoot 'config.ps1')
$c = Read-InfrastructureConfiguration $ConfigurationFile
if ($OperatorPrincipalArn -notmatch ("^arn:aws:iam::"+[regex]::Escape($c.account)+":(user|role)/[A-Za-z0-9_+=,.@/-]+$")) { throw 'Exact confirmed non-root operator ARN required.' }
if ($OperatorPrincipalArn -eq "arn:aws:iam::$($c.account):role/$($c.seed_role)" -or $OperatorPrincipalArn -like "*role$($c.role_path)*") { throw 'Seed/project roles cannot be the operator.' }
# Offline readiness examples must be unusable, even if copied into the console.
# Normal owner execution still requires a future approved window; this switch
# permits only expired dates and never relaxes the duration/principal checks.
if ($WindowEndUtc -le $WindowStartUtc -or ($WindowEndUtc-$WindowStartUtc).TotalHours -gt 1 -or
    (!$ExpiredExample -and $WindowEndUtc -le [DateTimeOffset]::UtcNow) -or
    ($ExpiredExample -and $WindowEndUtc -gt [DateTimeOffset]::UtcNow)) { throw 'Use an approved future window <=1h, or an expired example with -ExpiredExample.' }
# No implicit owner destination: offline callers must choose their own fixture
# directory and cannot replace prepared deployment policies by omitting a flag.
$taskOut = [IO.Path]::GetFullPath($OutputDirectory, $PWD.ProviderPath)
New-Item -ItemType Directory -Force $taskOut | Out-Null
$a = $c.account; $r = $c.region
$taskBoundaryArn = "arn:aws:iam::${a}:policy/$($c.boundary_name)"
$taskRoles = @($c.roles | ForEach-Object { "arn:aws:iam::${a}:role$($c.role_path)$($c.project)-$_" })
$taskIssuer = "arn:aws:iam::${a}:role$($c.role_path)$($c.project)-issuer"
$taskMedia = "arn:aws:iam::${a}:role$($c.role_path)$($c.project)-media"
$taskTable = "arn:aws:dynamodb:${r}:${a}:table/$($c.table)"
$taskStreams = @(1..$c.video_stream_count | ForEach-Object { "arn:aws:kinesisvideo:${r}:${a}:stream/$($c.project)-slot-$('{0:D2}' -f $_)/*" })
$taskState = "arn:aws:s3:::$($c.state_bucket)"
$taskRecording = "arn:aws:s3:::$($c.recording_bucket)"
$taskFunction = "arn:aws:lambda:${r}:${a}:function:$($c.function)"
$taskLogs = @("arn:aws:logs:${r}:${a}:log-group:/aws/lambda/$($c.function)","arn:aws:logs:${r}:${a}:log-group:/$($c.project)/api-access")
$taskSecret = "arn:aws:secretsmanager:${r}:${a}:secret:$($c.secret)-??????"
$taskTopic = "arn:aws:sns:${r}:${a}:$($c.topic)"
$taskSchedule = "arn:aws:events:${r}:${a}:rule/$($c.schedule)"
$taskApi = "arn:aws:apigateway:${r}::/apis/$ApiId"
$taskApiTags = "arn:aws:apigateway:${r}::/tags/" + [Uri]::EscapeDataString($taskApi)
# The pre-ID authorization resource ends in a literal '*', not an API ID.
# AWS's predefined ${*} substitution matches that character without granting a
# wildcard over existing IDs/descendants. Append it after URI encoding, and use
# a single-quoted PowerShell string so the IAM syntax survives JSON rendering.
$taskCreateApiTags = "arn:aws:apigateway:${r}::/tags/" + [Uri]::EscapeDataString("arn:aws:apigateway:${r}::/v2/apis/") + '${*}'
$taskCreateStageTags = "arn:aws:apigateway:${r}::/tags/" + [Uri]::EscapeDataString("arn:aws:apigateway:${r}::/v2/apis/$ApiId/stages/") + '${*}'
$taskCreateTagConditions = @{
    StringEquals=@{'aws:RequestTag/Project'=$c.project;'aws:RequestTag/ManagedBy'='Terraform';'aws:RequestTag/Component'='recording'}
    'ForAllValues:StringEquals'=@{'aws:TagKeys'=@('Project','ManagedBy','Component')}
}
$taskSignin = @{ Effect='Allow'; Action=@('signin:AuthorizeOAuth2Access','signin:CreateOAuth2Token'); Resource="arn:aws:signin:${r}:${a}:oauth2/public-client/localhost" }
function Save-Policy($name, $statements) {
    $doc = @{ Version='2012-10-17'; Statement=@($statements) }
    $json = $doc | ConvertTo-Json -Depth 30
    $json | Set-Content -LiteralPath (Join-Path $taskOut $name) -Encoding utf8NoBOM
    return ($doc | ConvertTo-Json -Depth 30 -Compress).Length
}
$taskWindowDenies = @(
    @{Effect='Deny';Action='*';Resource='*';Condition=@{DateLessThan=@{'aws:CurrentTime'=$WindowStartUtc.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')}}},
    @{Effect='Deny';Action='*';Resource='*';Condition=@{DateGreaterThanEquals=@{'aws:CurrentTime'=$WindowEndUtc.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')}}}
)
$taskMfa = if ($OperatorPrincipalArn -match ':user/') { @{Bool=@{'aws:MultiFactorAuthPresent'='true'}} } else { @{} }
$taskMfa.DateGreaterThanEquals = @{'aws:CurrentTime'=$WindowStartUtc.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')}
$taskMfa.DateLessThan = @{'aws:CurrentTime'=$WindowEndUtc.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')}
$null = Save-Policy 'bootstrap-trust.json' @(@{Effect='Allow';Principal=@{AWS=$OperatorPrincipalArn};Action='sts:AssumeRole';Condition=$taskMfa})
$null = Save-Policy 'operator-assume.json' @(@{Effect='Allow';Action='sts:AssumeRole';Resource=@("arn:aws:iam::${a}:role/$($c.seed_role)") + @($taskRoles | Where-Object { $_ -match ('/' + [regex]::Escape($c.project) + '-(deploy|retrieval|operations)$') })})
# Phase one has no child IAM role/policy authority. Terraform creates the named
# empty API before any ceiling is rendered. Project-tag checks constrain reads
# during this phase; the exact returned ID scopes all phase-two API permissions.
$taskFoundation = @(
    $taskSignin,
    @{Effect='Allow';Action=@('apigateway:POST');Resource="arn:aws:apigateway:${r}::/apis";Condition=@{StringEquals=@{'apigateway:Request/ApiName'=$c.api;'aws:RequestTag/Project'=$c.project}}},
    # CreateApi with Tags also authorizes POST on this synthetic /v2 ARN before
    # an API ID exists (observed 403). Tags supports RequestTag/TagKeys, not
    # Request/ApiName or ResourceTag; retain the name guard on POST /apis above.
    # ${*} matches only the literal pre-ID placeholder, so neither canonical
    # existing APIs nor concrete /v2 API IDs/descendants match this addition.
    @{Sid='FoundationCreateApiTags';Effect='Allow';Action=@('apigateway:POST');Resource=$taskCreateApiTags;Condition=$taskCreateTagConditions},
    @{Effect='Allow';Action=@('apigateway:GET');Resource=@("arn:aws:apigateway:${r}::/apis/*","arn:aws:apigateway:${r}::/tags/*");Condition=@{StringEquals=@{'aws:ResourceTag/Project'=$c.project}}},
    @{Effect='Allow';Action=@('iam:CreateServiceLinkedRole');Resource="arn:aws:iam::${a}:role/aws-service-role/ops.apigateway.amazonaws.com/AWSServiceRoleForAPIGateway";Condition=@{StringEquals=@{'iam:AWSServiceName'='ops.apigateway.amazonaws.com'}}},
    @{Effect='Allow';Action=@('iam:GetRole');Resource="arn:aws:iam::${a}:role/aws-service-role/ops.apigateway.amazonaws.com/AWSServiceRoleForAPIGateway"}
) + $taskWindowDenies
$null = Save-Policy 'foundation-permissions.json' $taskFoundation
if (!$ApiId) {
    Write-Output "Prepared foundation-only console policies in $taskOut; no child IAM authority/boundary or AWS changes."
    return
}
# This ceiling is created/owned through the console, never managed by bootstrap.
# A child role cannot acquire IAM policy/trust/boundary mutation even if its inline
# policy is accidentally broadened. Actual role policies are substantially narrower.
$taskBoundary = @(
    $taskSignin,
    @{Effect='Allow';Action=@('dynamodb:GetItem','dynamodb:PutItem','dynamodb:UpdateItem','dynamodb:Query');Resource=@($taskTable,"$taskTable/index/sessions-by-day")},
    @{Effect='Allow';Action=@('kinesisvideo:DescribeStream','kinesisvideo:GetDataEndpoint','kinesisvideo:PutMedia','kinesisvideo:ListFragments','kinesisvideo:GetMediaForFragmentList','kinesisvideo:GetClip');Resource=$taskStreams},
    @{Effect='Allow';Action=@('s3:ListBucket');Resource=@($taskState,$taskRecording)},
    @{Effect='Allow';Action=@('s3:GetObject','s3:PutObject');Resource=@("$taskRecording/sessions/*","$taskState/runtime/terraform.tfstate")},
    @{Effect='Allow';Action=@('s3:GetObject','s3:PutObject','s3:DeleteObject');Resource="$taskState/runtime/terraform.tfstate.tflock"},
    @{Effect='Allow';Action=@('secretsmanager:GetSecretValue');Resource=$taskSecret},
    @{Effect='Allow';Action=@('sts:AssumeRole');Resource=$taskMedia},
    @{Effect='Allow';Action=@('logs:CreateLogStream','logs:PutLogEvents');Resource=@($taskLogs | ForEach-Object { "${_}:log-stream:*" })},
    @{Effect='Allow';Action=@('lambda:CreateFunction','lambda:GetFunction','lambda:GetFunctionConfiguration','lambda:GetFunctionCodeSigningConfig','lambda:UpdateFunctionCode','lambda:UpdateFunctionConfiguration','lambda:PutFunctionConcurrency','lambda:GetFunctionConcurrency','lambda:ListTags','lambda:TagResource','lambda:UntagResource','lambda:ListVersionsByFunction','lambda:GetRuntimeManagementConfig');Resource=$taskFunction},
    @{Effect='Allow';Action=@('iam:PassRole');Resource=$taskIssuer;Condition=@{StringEquals=@{'iam:PassedToService'='lambda.amazonaws.com'}}},
    @{Effect='Allow';Action=@('apigateway:GET','apigateway:POST','apigateway:PATCH','apigateway:DELETE');Resource=@($taskApi,"$taskApi/*",$taskApiTags,"${taskApiTags}%2F*")}
)
$taskBoundaryLength = Save-Policy 'boundary.json' $taskBoundary
if ($taskBoundaryLength -gt 6144) { throw 'Boundary exceeds managed policy quota.' }

# IAM mutation enumerates five child roles; CreateRole must supply the immutable
# ceiling. There is no seed/operator/boundary mutation or policy-attachment path.
$taskSeed = @(
    $taskSignin,
    @{Effect='Allow';Action=@('iam:CreateRole');Resource=$taskRoles;Condition=@{ArnEquals=@{'iam:PermissionsBoundary'=$taskBoundaryArn}}},
    @{Effect='Allow';Action=@('iam:GetRole','iam:UpdateAssumeRolePolicy','iam:UpdateRole','iam:TagRole','iam:UntagRole','iam:ListRolePolicies','iam:GetRolePolicy','iam:PutRolePolicy','iam:DeleteRolePolicy','iam:ListAttachedRolePolicies','iam:ListInstanceProfilesForRole');Resource=$taskRoles},
    @{Effect='Allow';Action=@('iam:GetPolicy','iam:GetPolicyVersion');Resource=$taskBoundaryArn},
    @{Effect='Allow';Action=@('iam:GetRole');Resource="arn:aws:iam::${a}:role/aws-service-role/ops.apigateway.amazonaws.com/AWSServiceRoleForAPIGateway"},
    # aws_s3_bucket in provider6.69.0 refreshes these legacy settings even though
    # separate Terraform resources own our security/lifecycle configuration.
    # Read-only ACL/CORS/website/acceleration/payment/logging/replication/lock
    # access is needed for refresh; no corresponding mutation is granted.
    @{Effect='Allow';Action=@('s3:CreateBucket','s3:ListBucket','s3:GetBucketLocation','s3:GetBucketTagging','s3:PutBucketTagging','s3:GetBucketVersioning','s3:PutBucketVersioning','s3:GetEncryptionConfiguration','s3:PutEncryptionConfiguration','s3:GetBucketPublicAccessBlock','s3:PutBucketPublicAccessBlock','s3:GetBucketOwnershipControls','s3:PutBucketOwnershipControls','s3:GetBucketPolicy','s3:PutBucketPolicy','s3:GetLifecycleConfiguration','s3:PutLifecycleConfiguration','s3:GetBucketAcl','s3:GetBucketCORS','s3:GetBucketWebsite','s3:GetAccelerateConfiguration','s3:GetBucketRequestPayment','s3:GetBucketLogging','s3:GetReplicationConfiguration','s3:GetBucketObjectLockConfiguration');Resource=@($taskState,$taskRecording)},
    @{Effect='Allow';Action=@('s3:GetObject','s3:PutObject');Resource=@("$taskState/foundation/terraform.tfstate","$taskState/bootstrap/terraform.tfstate","$taskState/runtime/terraform.tfstate")},
    @{Effect='Allow';Action=@('s3:GetObject','s3:PutObject','s3:DeleteObject');Resource=@("$taskState/foundation/terraform.tfstate.tflock","$taskState/bootstrap/terraform.tfstate.tflock","$taskState/runtime/terraform.tfstate.tflock")},
    @{Effect='Allow';Action=@('dynamodb:CreateTable','dynamodb:DescribeTable','dynamodb:UpdateTable','dynamodb:DescribeTimeToLive','dynamodb:UpdateTimeToLive','dynamodb:DescribeContinuousBackups','dynamodb:ListTagsOfResource','dynamodb:TagResource','dynamodb:UntagResource');Resource=$taskTable},
    @{Effect='Allow';Action=@('kinesisvideo:CreateStream','kinesisvideo:DescribeStream','kinesisvideo:UpdateDataRetention','kinesisvideo:ListTagsForStream','kinesisvideo:TagStream','kinesisvideo:UntagStream');Resource=$taskStreams},
    @{Effect='Allow';Action=@('secretsmanager:CreateSecret','secretsmanager:DescribeSecret','secretsmanager:TagResource','secretsmanager:UntagResource','secretsmanager:GetResourcePolicy');Resource=$taskSecret},
    @{Effect='Allow';Action=@('logs:CreateLogGroup','logs:PutRetentionPolicy','logs:ListTagsForResource','logs:TagResource','logs:UntagResource');Resource=@($taskLogs | ForEach-Object { $_; "${_}:*" })},
    # DescribeLogGroups cannot be resource-scoped. Read-only names/retention metadata.
    @{Effect='Allow';Action=@('logs:DescribeLogGroups');Resource='*';Condition=@{StringEquals=@{'aws:RequestedRegion'=$r}}},
    @{Effect='Allow';Action=@('apigateway:GET','apigateway:PATCH');Resource=@($taskApi,"$taskApi/*")},
    @{Effect='Allow';Action=@('apigateway:POST');Resource="$taskApi/stages"},
    @{Effect='Allow';Action=@('apigateway:GET','apigateway:POST','apigateway:DELETE');Resource=@($taskApiTags,"${taskApiTags}%2F*")},
    # CreateStage sends Tags in the same v2 creation API family. Its synthetic
    # tag authorization follows the evidenced CreateApi shape, now with the
    # allocated API ID fixed. Only bootstrap needs this, never the deploy role.
    @{Sid='BootstrapCreateStageTags';Effect='Allow';Action=@('apigateway:POST');Resource=$taskCreateStageTags;Condition=$taskCreateTagConditions},
    @{Effect='Allow';Action=@('lambda:GetPolicy','lambda:AddPermission','lambda:RemovePermission');Resource=$taskFunction},
    @{Effect='Allow';Action=@('events:PutRule','events:DescribeRule','events:ListTagsForResource','events:TagResource','events:UntagResource','events:PutTargets','events:ListTargetsByRule','events:RemoveTargets');Resource=$taskSchedule},
    @{Effect='Allow';Action=@('sns:CreateTopic','sns:GetTopicAttributes','sns:SetTopicAttributes','sns:ListTagsForResource','sns:TagResource','sns:UntagResource','sns:Subscribe','sns:ListSubscriptionsByTopic');Resource=$taskTopic},
    # SNS authorizes subscription attribute/unsubscribe calls against the topic,
    # not the subscription ARN returned by Subscribe or read by the provider.
    @{Effect='Allow';Action=@('sns:GetSubscriptionAttributes','sns:Unsubscribe');Resource=$taskTopic},
    @{Effect='Allow';Action=@('cloudwatch:PutMetricAlarm','cloudwatch:DescribeAlarms','cloudwatch:ListTagsForResource','cloudwatch:TagResource','cloudwatch:UntagResource');Resource=@("arn:aws:cloudwatch:${r}:${a}:alarm:$($c.project)-errors","arn:aws:cloudwatch:${r}:${a}:alarm:$($c.project)-throttles")},
    @{Effect='Allow';Action=@('budgets:ViewBudget','budgets:ModifyBudget','budgets:ListTagsForResource','budgets:TagResource','budgets:UntagResource');Resource="arn:aws:budgets::${a}:budget/$($c.budget)"}
)
# Enforce the window on already issued sessions too, not just future AssumeRole.
$taskSeed += $taskWindowDenies
$taskSeed += @{Effect='Deny';Action=@('secretsmanager:GetSecretValue','secretsmanager:PutSecretValue','secretsmanager:UpdateSecret');Resource='*'}
# Split owner inline policies below the aggregate 10,240-character role quota;
# no broad managed-policy attachment or AdministratorAccess is needed.
$taskSeedLength = Save-Policy 'bootstrap-permissions.json' $taskSeed
if ($taskSeedLength -gt 10240) { throw 'Bootstrap policy exceeds inline role quota.' }
@{configuration_file=[IO.Path]::GetFullPath($ConfigurationFile);operator_principal_arn=$OperatorPrincipalArn;api_id=$ApiId;runtime_ready=$false;owner_logging_ready=$false} | ConvertTo-Json | Set-Content (Join-Path $taskOut 'bootstrap.tfvars.json')
@{BoundaryCharacters=$taskBoundaryLength;BootstrapCharacters=$taskSeedLength;WindowStart=$WindowStartUtc;WindowEnd=$WindowEndUtc;Operator=$OperatorPrincipalArn;ApiId=$ApiId;SeedRole=$c.seed_role} | ConvertTo-Json | Set-Content (Join-Path $taskOut 'inventory.json')
Write-Output "Prepared console-owned policies in $taskOut; no account changes."
