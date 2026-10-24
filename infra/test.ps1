$ErrorActionPreference='Stop'
$tf=if($env:PLAYKITU_TERRAFORM){[IO.Path]::GetFullPath($env:PLAYKITU_TERRAFORM)}else{Join-Path $PSScriptRoot 'private/terraform/terraform.exe'}
if(!(Test-Path $tf)){throw 'Run infra/prepare.ps1 or select the pinned Terraform executable.'}
if((& $tf version -json | ConvertFrom-Json).terraform_version -ne '1.16.6'){throw 'Pinned Terraform 1.16.6 required.'}
$evidence=Join-Path $PSScriptRoot 'private/tests'
New-Item $evidence -ItemType Directory -Force | Out-Null
$env:CHECKPOINT_DISABLE='1';$env:TF_IN_AUTOMATION='1';$env:AWS_EC2_METADATA_DISABLED='true'
$env:AWS_ACCESS_KEY_ID='OFFLINE_TEST_ONLY';$env:AWS_SECRET_ACCESS_KEY='OFFLINE_TEST_ONLY';$env:AWS_SESSION_TOKEN='OFFLINE_TEST_ONLY'
$env:AWS_CONFIG_FILE=Join-Path $evidence 'empty-config';$env:AWS_SHARED_CREDENTIALS_FILE=Join-Path $evidence 'empty-credentials'
'' | Set-Content $env:AWS_CONFIG_FILE
'' | Set-Content $env:AWS_SHARED_CREDENTIALS_FILE
$env:AWS_ENDPOINT_URL='http://127.0.0.1:9'
$c=Get-Content "$PSScriptRoot/deployment.example.json" -Raw | ConvertFrom-Json -AsHashtable
$c.awsAccountId='210987654321';$c.awsRegion='eu-west-1';$c.steamAppId=654321
$c | ConvertTo-Json | Set-Content "$evidence/deployment-second.json"
$eight=Get-Content "$PSScriptRoot/deployment.example.json" -Raw|ConvertFrom-Json -AsHashtable
$eight.videoStreamCount=8
$eight|ConvertTo-Json|Set-Content "$evidence/deployment-eight.json"
& $tf fmt -check -recursive $PSScriptRoot
if($LASTEXITCODE){throw 'Terraform format check failed.'}
foreach($root in 'foundation','bootstrap','runtime'){
 & $tf "-chdir=$PSScriptRoot/$root" init -backend=false -input=false -no-color *> "$evidence/$root-init.log"
 if($LASTEXITCODE){throw "Backend-disabled init failed: $root"}
 & $tf "-chdir=$PSScriptRoot/$root" validate -no-color *> "$evidence/$root-validate.log"
 if($LASTEXITCODE){Get-Content "$evidence/$root-validate.log";throw "Validation failed: $root"}
 # Defense in depth: tests are mocked and all outbound HTTP(S) is blocked.
 $oldHttp=$env:HTTP_PROXY;$oldHttps=$env:HTTPS_PROXY;$oldNoProxy=$env:NO_PROXY
 try {
  $env:HTTP_PROXY='http://127.0.0.1:9';$env:HTTPS_PROXY='http://127.0.0.1:9';$env:NO_PROXY=''
  & $tf "-chdir=$PSScriptRoot/$root" test -no-color *> "$evidence/$root-test.log"
 } finally {$env:HTTP_PROXY=$oldHttp;$env:HTTPS_PROXY=$oldHttps;$env:NO_PROXY=$oldNoProxy}
 Get-Content "$evidence/$root-test.log" -Tail 5
 if($LASTEXITCODE){throw "Mock Terraform tests failed: $root"}
}
. "$PSScriptRoot/config.ps1"
$settings=Read-InfrastructureConfiguration "$PSScriptRoot/deployment.example.json"
$owner=Join-Path $evidence ('policies-'+[guid]::NewGuid().ToString('N'))
& "$PSScriptRoot/access/prepare-owner.ps1" -ConfigurationFile "$PSScriptRoot/deployment.example.json" -OperatorPrincipalArn "arn:aws:iam::$($settings.account):user/offline-fixture" -ApiId abc123def4 -WindowStartUtc ([DateTimeOffset]'2000-01-01Z') -WindowEndUtc ([DateTimeOffset]'2000-01-01T00:30:00Z') -ExpiredExample -OutputDirectory $owner
& "$PSScriptRoot/access/prepare-control.ps1" -ConfigurationFile "$PSScriptRoot/deployment.example.json" -OutputDirectory "$owner/controls"
$seed=Get-Content "$owner/controls/control-seed.json" -Raw | ConvertFrom-Json -AsHashtable
if($seed.Item.admissionsEnabled.BOOL -or $seed.Item.uploadsEnabled.BOOL -or ($seed.Item.data.S | ConvertFrom-Json).slots.Count -ne 4 -or $seed.ConditionExpression -ne 'attribute_not_exists(#pk)'){throw 'Canonical OFF seed protections changed.'}
$deploy=Get-Content "$PSScriptRoot/bootstrap/iam.tf" -Raw
if($deploy -match 'secretsmanager:PutSecretValue|AdministratorAccess'){throw 'Routine role authority expanded.'}
$ownerBoundary=Get-Content "$owner/boundary.json" -Raw|ConvertFrom-Json -AsHashtable
$concurrency=@($ownerBoundary.Statement|Where-Object {'lambda:DeleteFunctionConcurrency'-in$_.Action})
if($concurrency.Count-ne 1-or$concurrency[0].Resource-ne"arn:aws:lambda:$($settings.region):$($settings.account):function:$($settings.function)"-or@($ownerBoundary.Statement|Where-Object {'lambda:DeleteFunction'-in$_.Action}).Count-ne 0){throw 'Owner boundary must allow removing only the named function concurrency limit, not deleting the function.'}
$fixture=Get-Content "$PSScriptRoot/deployment.example.json" -Raw | ConvertFrom-Json -AsHashtable
$fixture.awsAccountId='bad';$fixture | ConvertTo-Json | Set-Content "$evidence/invalid.json"
$rejected=$false;try{Read-InfrastructureConfiguration "$evidence/invalid.json"}catch{$rejected=$true}
if(!$rejected){throw 'Owner settings trust rejection failed.'}
foreach($count in @(0,9,1.5,'4',$true)){
 $invalidPool=Get-Content "$PSScriptRoot/deployment.example.json" -Raw|ConvertFrom-Json -AsHashtable
 $invalidPool.videoStreamCount=$count;$invalidPool|ConvertTo-Json|Set-Content "$evidence/invalid-pool.json"
 $rejected=$false;try{Read-InfrastructureConfiguration "$evidence/invalid-pool.json"|Out-Null}catch{$rejected=$true}
 if(!$rejected){throw 'Invalid video stream count accepted.'}
}
if((Read-InfrastructureConfiguration "$evidence/deployment-eight.json").video_stream_count -ne 8){throw 'Configured video pool mismatch.'}
# Exercise the actual PowerShell preparation path as well as mocked Terraform.
# An empty on-demand pool must not generate legacy slot permissions or seed maps.
$onDemandFile=Join-Path $PSScriptRoot 'deployment.on-demand.example.json'
$onDemand=Read-InfrastructureConfiguration $onDemandFile
if($onDemand.stream_mode-cne'onDemand'-or$onDemand.video_stream_count-ne 0-or$null-ne$onDemand.recording_limits.concurrentSessions-or$null-ne$onDemand.lambda_concurrency){throw 'On-demand defaults changed.'}
$onDemandOwner=Join-Path $owner 'on-demand'
& "$PSScriptRoot/access/prepare-owner.ps1" -ConfigurationFile $onDemandFile -OperatorPrincipalArn "arn:aws:iam::$($onDemand.account):user/offline-fixture" -ApiId abc123def4 -WindowStartUtc ([DateTimeOffset]'2000-01-01Z') -WindowEndUtc ([DateTimeOffset]'2000-01-01T00:30:00Z') -ExpiredExample -OutputDirectory $onDemandOwner
& "$PSScriptRoot/access/prepare-control.ps1" -ConfigurationFile $onDemandFile -OutputDirectory "$onDemandOwner/controls"
$onDemandSeed=Get-Content "$onDemandOwner/controls/control-seed.json" -Raw|ConvertFrom-Json -AsHashtable
$onDemandState=$onDemandSeed.Item.data.S|ConvertFrom-Json
if($onDemandSeed.Item.admissionsEnabled.BOOL-or$onDemandSeed.Item.uploadsEnabled.BOOL-or$onDemandState.schemaVersion-ne 2-or$onDemandState.slots.Count-ne 0-or$onDemandSeed.ConditionExpression-ne'attribute_not_exists(#pk)'){throw 'On-demand seed must be empty, schema 2 and conditionally OFF.'}
$boundary=Get-Content "$onDemandOwner/boundary.json" -Raw|ConvertFrom-Json -AsHashtable
$create=@($boundary.Statement|Where-Object {'kinesisvideo:CreateStream'-in$_.Action})
$delete=@($boundary.Statement|Where-Object {'kinesisvideo:DeleteStream'-in$_.Action})
if($create.Count-ne 1-or$delete.Count-ne 1-or$create[0].Condition.StringEquals.'aws:RequestTag/Project'-ne$onDemand.project-or$delete[0].Condition.StringEquals.'aws:ResourceTag/Project'-ne$onDemand.project-or$create[0].Resource-notlike'*session-*'){throw 'On-demand boundary lost namespace/tag guards.'}
foreach($case in @(@{concurrentSessions=0},@{admissionsPerDay=-1},@{admissionsPerMonth='50'},@{testerAdmissionsPerDay=$true},@{admissionAttemptsPerMinute=$null},@{sessionRequestsPerMinute=121},@{unexpected=1})){
 $invalid=Get-Content $onDemandFile -Raw|ConvertFrom-Json -AsHashtable
 $invalid.recordingLimits=$case
 $invalid|ConvertTo-Json -Depth 8|Set-Content "$evidence/invalid-limit.json"
 $rejected=$false;try{Read-InfrastructureConfiguration "$evidence/invalid-limit.json"|Out-Null}catch{$rejected=$true}
 if(!$rejected){throw 'Invalid recording limit accepted.'}
}
@{Passed=$true;MockProviderOnly=$true;NoAwsCalls=$true;ControlSeedOff=$true;Evidence=$evidence} | ConvertTo-Json | Set-Content "$evidence/result.json"
& pwsh -NoProfile -File "$PSScriptRoot/access/test-operations.ps1" *> "$evidence/operations-test.log"
if($LASTEXITCODE){Get-Content "$evidence/operations-test.log";throw 'Offline operation guards failed.'}
Get-Content "$evidence/operations-test.log" -Tail 2
Write-Output 'Terraform validation/mock suites and offline owner/OFF-seed checks passed.'
