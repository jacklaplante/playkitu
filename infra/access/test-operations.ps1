$ErrorActionPreference='Stop'
. "$PSScriptRoot/operations.ps1"
$fixtureRoot=Join-Path $PSScriptRoot ('../private/operations-tests/'+[guid]::NewGuid().ToString('N'))
New-Item $fixtureRoot -ItemType Directory -Force | Out-Null
$configuration=[IO.Path]::GetFullPath("$PSScriptRoot/../deployment.example.json")
$env:AWS_CONFIG_FILE=Join-Path $fixtureRoot 'empty-config'
$env:AWS_SHARED_CREDENTIALS_FILE=Join-Path $fixtureRoot 'empty-credentials'
$env:AWS_LOGIN_CACHE_DIRECTORY=Join-Path $fixtureRoot 'empty-cache'
'' | Set-Content $env:AWS_CONFIG_FILE
'' | Set-Content $env:AWS_SHARED_CREDENTIALS_FILE
New-Item $env:AWS_LOGIN_CACHE_DIRECTORY -ItemType Directory | Out-Null
foreach($key in @('AWS_ACCESS_KEY_ID','AWS_SECRET_ACCESS_KEY','AWS_SESSION_TOKEN')+@(Get-ChildItem Env: | Where-Object Name -like 'AWS_ENDPOINT_URL*' | ForEach-Object Name)){Remove-Item "Env:$key" -ErrorAction SilentlyContinue}
$script:identityAccount='123456789012';$script:wrongRole=$false;$script:updateExit=0;$script:badFlags=$false;$script:unknownState=$false
$script:daily=2;$script:monthly=12;$script:tester=3;$script:active=@{};$script:code=('1'*64)
$script:calls=[Collections.Generic.List[string]]::new()
# The function wins command resolution over any real executable. No SDK/profile
# provider or network transport is created by these local owner-helper tests.
function aws {
    param([Parameter(ValueFromRemainingArguments)][object[]]$Arguments)
    $script:calls.Add(($Arguments -join ' '));$global:LASTEXITCODE=0
    if($Arguments[0] -eq 'sts'){
        $role=if('deploy' -in $Arguments){'deploy'}elseif($script:wrongRole){'deploy'}else{'operations'}
        return (@{account=$script:identityAccount;arn="arn:aws:sts::$($script:identityAccount):assumed-role/playkitu-$role/offline"} | ConvertTo-Json -Compress)
    }
    if($Arguments[0] -eq 'lambda'){return [Convert]::ToBase64String([Convert]::FromHexString($script:code))}
    if($Arguments[1] -eq 'update-item'){$global:LASTEXITCODE=$script:updateExit;return}
    if('--cli-input-json' -in $Arguments){return $(if($script:badFlags){'{"admissions":null,"uploads":false}'}else{'{"admissions":false,"uploads":false}'})}
    $state=@{day=[DateTimeOffset]::UtcNow.ToString('yyyy-MM-dd');month=[DateTimeOffset]::UtcNow.ToString('yyyy-MM');dailyAdmissions=$script:daily;monthlyAdmissions=$script:monthly;testerDailyAdmissions=@{fake=$script:tester};active=$script:active;pending=@{}}
    if($script:unknownState){$state.Remove('day')}
    return (@{Item=@{admissionsEnabled=@{BOOL=$false};uploadsEnabled=@{BOOL=$false};data=@{S=($state | ConvertTo-Json -Compress -Depth 8)}}} | ConvertTo-Json -Compress -Depth 10)
}
$checks=0
function Check([string]$Name,[bool]$Condition){if(!$Condition){throw "Offline operation check failed: $Name"};$script:checks++}
function Reject([string]$Name,[scriptblock]$Operation){$failed=$false;try{& $Operation | Out-Null}catch{$failed=$true};Check $Name $failed}
Assert-PlaykituRole $configuration ops operations
$script:identityAccount='210987654321'
Reject 'wrong account rejected' {Assert-PlaykituRole $configuration ops operations}
$script:identityAccount='123456789012';$script:wrongRole=$true
Reject 'wrong role rejected' {Assert-PlaykituRole $configuration ops operations}
$script:wrongRole=$false
$env:AWS_ACCESS_KEY_ID='OFFLINE_TEST_ONLY'
Reject 'ambient static credentials rejected' {Assert-PlaykituRole $configuration ops operations}
Remove-Item Env:AWS_ACCESS_KEY_ID
$env:AWS_ENDPOINT_URL_STS='http://127.0.0.1:9'
Reject 'service-specific endpoint override rejected' {Assert-PlaykituRole $configuration ops operations}
Remove-Item Env:AWS_ENDPOINT_URL_STS
$allowanceParameters=@{ConfigurationFile=$configuration;OperationsProfile='ops';DeployProfile='deploy';PlannedAdmissions=1;ReviewedIssuerSha256=('1'*64)}
$a=Get-PlaykituAllowance @allowanceParameters
Check 'finite conservative allowance' ($a.AllowanceAvailable -and $a.GlobalDailyRemaining -eq 6 -and $a.GlobalMonthlyRemaining -eq 38 -and $a.ConservativeTesterRemaining -eq 5)
$onDemandParameters=$allowanceParameters.Clone()
$onDemandParameters.ConfigurationFile=[IO.Path]::GetFullPath("$PSScriptRoot/../deployment.on-demand.example.json")
$beforeOnDemand=$script:calls.Count
Reject 'legacy allowance cannot invent on-demand quota remaining' {Get-PlaykituAllowance @onDemandParameters}
Check 'on-demand legacy allowance rejection needs no AWS call' ($script:calls.Count-eq$beforeOnDemand)
$script:daily=8;Check 'global daily ceiling' (!(Get-PlaykituAllowance @allowanceParameters).AllowanceAvailable);$script:daily=2
$script:monthly=50;Check 'global monthly ceiling' (!(Get-PlaykituAllowance @allowanceParameters).AllowanceAvailable);$script:monthly=12
$script:tester=8;Check 'tester ceiling' (!(Get-PlaykituAllowance @allowanceParameters).AllowanceAvailable);$script:tester=3
$script:active=@{fake=@{}};Check 'stored lease prevents activation' (!(Get-PlaykituAllowance @allowanceParameters).AllowanceAvailable);$script:active=@{}
$script:unknownState=$true;Reject 'unknown calendar state fails closed' {Get-PlaykituAllowance @allowanceParameters};$script:unknownState=$false
$script:code=('2'*64);Reject 'unreviewed live issuer rejected' {Get-PlaykituAllowance @allowanceParameters};$script:code=('1'*64)
& "$PSScriptRoot/prepare-control.ps1" -ConfigurationFile $configuration -OutputDirectory "$fixtureRoot/controls"
$control=@{ConfigurationFile=$configuration;Profile='ops';Request="$fixtureRoot/controls/control-switch.json";ReadRequest="$fixtureRoot/controls/control-read.json";AdmissionsEnabled=$false;UploadsEnabled=$false}
$v=Set-PlaykituControls @control
Check 'strong actual OFF booleans' ($v.admissions -is [bool] -and !$v.admissions -and $v.uploads -is [bool] -and !$v.uploads)
$script:badFlags=$true;Reject 'null strong read is not OFF' {Set-PlaykituControls @control};$script:badFlags=$false
$script:updateExit=1;Reject 'failed update is not success' {Set-PlaykituControls @control};$script:updateExit=0
$change=Get-Content $control.Request -Raw | ConvertFrom-Json -AsHashtable
$change.ReturnValues='ALL_NEW';$change | ConvertTo-Json -Depth 10 | Set-Content $control.Request
$before=$script:calls.Count;Reject 'unsafe request cannot return raw CONTROL' {Set-PlaykituControls @control}
Check 'unsafe change never reaches DynamoDB' (@($script:calls | Select-Object -Skip $before | Where-Object {$_ -like 'dynamodb*'}).Count -eq 0)
# A second project must retain the exact routine-role scope, not silently
# lose access or expand it to issuer/media. Everything below only renders JSON.
$other=Get-Content $configuration -Raw | ConvertFrom-Json -AsHashtable -DateKind String
$other.project='field-capture';$other.rolePath='/capture-tools/';$other.awsAccountId='210987654321';$other.awsRegion='eu-west-1';$other.steamAppId=654321
$otherConfig=Join-Path $fixtureRoot 'second-project.json'
$other | ConvertTo-Json | Set-Content $otherConfig
$policyOut=Join-Path $fixtureRoot 'second-project-policies'
& "$PSScriptRoot/prepare-owner.ps1" -ConfigurationFile $otherConfig -OperatorPrincipalArn 'arn:aws:iam::210987654321:user/offline-fixture' -ApiId abc123def4 -WindowStartUtc ([DateTimeOffset]'2000-01-01T00:00:00Z') -WindowEndUtc ([DateTimeOffset]'2000-01-01T00:30:00Z') -ExpiredExample -OutputDirectory $policyOut
$assume=Get-Content "$policyOut/operator-assume.json" -Raw | ConvertFrom-Json -AsHashtable -DateKind String
$expected=@('arn:aws:iam::210987654321:role/field-capture-bootstrap')+@('deploy','retrieval','operations' | ForEach-Object {"arn:aws:iam::210987654321:role/capture-tools/field-capture-$_"})
$actual=@($assume.Statement[0].Resource)
Check 'second project exact bootstrap and three routine roles' ($actual.Count -eq 4 -and !(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)))
Check 'second project excludes issuer media and wildcard authority' (@($actual | Where-Object {$_ -match '-(issuer|media)$' -or $_ -match '[*?]'}).Count -eq 0)
$trust=Get-Content "$policyOut/bootstrap-trust.json" -Raw | ConvertFrom-Json -AsHashtable -DateKind String
Check 'second project exact owner MFA and expiry' ($trust.Statement.Count -eq 1 -and $trust.Statement[0].Principal.AWS -ceq 'arn:aws:iam::210987654321:user/offline-fixture' -and $trust.Statement[0].Condition.Bool['aws:MultiFactorAuthPresent'] -ceq 'true' -and $trust.Statement[0].Condition.DateGreaterThanEquals['aws:CurrentTime'] -ceq '2000-01-01T00:00:00Z' -and $trust.Statement[0].Condition.DateLessThan['aws:CurrentTime'] -ceq '2000-01-01T00:30:00Z')
$bootstrap=Get-Content "$policyOut/bootstrap-permissions.json" -Raw | ConvertFrom-Json -AsHashtable -DateKind String
$create=@($bootstrap.Statement | Where-Object {'iam:CreateRole' -in $_.Action})
$expectedChildren=@('issuer','media','deploy','retrieval','operations' | ForEach-Object {"arn:aws:iam::210987654321:role/capture-tools/field-capture-$_"})
Check 'second project create role ceiling remains exact' ($create.Count -eq 1 -and $create[0].Resource.Count -eq 5 -and !(Compare-Object ($expectedChildren | Sort-Object) ($create[0].Resource | Sort-Object)) -and $create[0].Condition.ArnEquals['iam:PermissionsBoundary'] -ceq 'arn:aws:iam::210987654321:policy/field-capture-boundary')
$beforeOwner=$script:calls.Count
Reject 'second project cannot use root operator' {& "$PSScriptRoot/prepare-owner.ps1" -ConfigurationFile $otherConfig -OperatorPrincipalArn 'arn:aws:iam::210987654321:root' -WindowStartUtc ([DateTimeOffset]'2000-01-01T00:00:00Z') -WindowEndUtc ([DateTimeOffset]'2000-01-01T00:30:00Z') -ExpiredExample -OutputDirectory "$fixtureRoot/root-must-not-exist"}
Check 'policy preparation never invoked AWS' ($script:calls.Count -eq $beforeOwner)
@{Passed=$true;Checks=$checks;FakeAwsOnly=$true;NoCredentialProvider=$true;Evidence=$fixtureRoot} | ConvertTo-Json | Set-Content "$fixtureRoot/result.json"
Write-Output "$checks offline owner-operation checks passed: $fixtureRoot"
