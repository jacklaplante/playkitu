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
$fixture=Get-Content "$PSScriptRoot/deployment.example.json" -Raw | ConvertFrom-Json -AsHashtable
$fixture.awsAccountId='bad';$fixture | ConvertTo-Json | Set-Content "$evidence/invalid.json"
$rejected=$false;try{Read-InfrastructureConfiguration "$evidence/invalid.json"}catch{$rejected=$true}
if(!$rejected){throw 'Owner settings trust rejection failed.'}
@{Passed=$true;MockProviderOnly=$true;NoAwsCalls=$true;ControlSeedOff=$true;Evidence=$evidence} | ConvertTo-Json | Set-Content "$evidence/result.json"
& pwsh -NoProfile -File "$PSScriptRoot/access/test-operations.ps1" *> "$evidence/operations-test.log"
if($LASTEXITCODE){Get-Content "$evidence/operations-test.log";throw 'Offline operation guards failed.'}
Get-Content "$evidence/operations-test.log" -Tail 2
Write-Output 'Terraform validation/mock suites and offline owner/OFF-seed checks passed.'
