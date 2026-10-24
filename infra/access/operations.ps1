# Definitions only. Sourcing this file never authenticates, reads or changes AWS.
. "$PSScriptRoot/../config.ps1"

function Assert-PlaykituRole([string]$ConfigurationFile,[string]$Profile,[ValidateSet('deploy','operations','retrieval')][string]$Role) {
    $c=Read-InfrastructureConfiguration $ConfigurationFile
    foreach($key in 'AWS_ACCESS_KEY_ID','AWS_SECRET_ACCESS_KEY','AWS_SESSION_TOKEN','AWS_ENDPOINT_URL'){
        if([Environment]::GetEnvironmentVariable($key)){throw 'Use isolated explicit process profiles; ambient credentials/endpoints are forbidden.'}
    }
    if(@(Get-ChildItem Env: | Where-Object Name -like 'AWS_ENDPOINT_URL*').Count){throw 'Ambient endpoint overrides are forbidden.'}
    foreach($key in 'AWS_CONFIG_FILE','AWS_SHARED_CREDENTIALS_FILE'){
        $path=[Environment]::GetEnvironmentVariable($key)
        if(!$path -or ![IO.Path]::IsPathFullyQualified($path) -or !(Test-Path -LiteralPath $path)){throw 'Explicit existing AWS profile paths required.'}
    }
    if(!$env:AWS_LOGIN_CACHE_DIRECTORY -or ![IO.Path]::IsPathFullyQualified($env:AWS_LOGIN_CACHE_DIRECTORY) -or !(Test-Path -LiteralPath $env:AWS_LOGIN_CACHE_DIRECTORY -PathType Container) -or !$Profile){throw 'Explicit process profile and login-cache directory required.'}
    $env:AWS_EC2_METADATA_DISABLED='true';$env:AWS_MAX_ATTEMPTS='1';$env:AWS_PAGER=''
    $text=& aws sts get-caller-identity --profile $Profile --region $c.region --query '{account:Account,arn:Arn}' --output json --cli-connect-timeout 5 --cli-read-timeout 10
    if($LASTEXITCODE){throw 'Role identity read failed.'}
    $i=($text -join '') | ConvertFrom-Json
    if($i.account -cne $c.account -or $i.arn -cnotmatch ('\Aarn:aws:sts::'+[regex]::Escape($c.account)+':assumed-role/'+[regex]::Escape("$($c.project)-$Role")+'/[A-Za-z0-9_+=,.@-]+\z')){throw 'Wrong owner account or role.'}
}

function Set-PlaykituControls {
    param([Parameter(Mandatory)][string]$ConfigurationFile,[Parameter(Mandatory)][string]$Profile,
        [Parameter(Mandatory)][string]$Request,[Parameter(Mandatory)][string]$ReadRequest,
        [Parameter(Mandatory)][bool]$AdmissionsEnabled,[Parameter(Mandatory)][bool]$UploadsEnabled)
    Assert-PlaykituRole $ConfigurationFile $Profile operations
    $c=Read-InfrastructureConfiguration $ConfigurationFile
    $change=Get-Content $Request -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    $read=Get-Content $ReadRequest -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    if($change.ReturnValues -cne 'NONE' -or $change.UpdateExpression -cne 'SET #ae = :ae, #ue = :ue' -or $change.ConditionExpression -cne 'attribute_exists(#pk) AND attribute_exists(#sk)' -or $change.TableName -cne $c.table -or $read.TableName -cne $c.table -or $read.ConsistentRead -ne $true -or
        $change.Key.pk.S -cne 'CONTROL' -or $change.Key.sk.S -cne 'META' -or $read.Key.pk.S -cne 'CONTROL' -or $read.Key.sk.S -cne 'META' -or
        $change.ExpressionAttributeValues[':ae'].BOOL -isnot [bool] -or $change.ExpressionAttributeValues[':ue'].BOOL -isnot [bool] -or
        $change.ExpressionAttributeValues[':ae'].BOOL -ne $AdmissionsEnabled -or $change.ExpressionAttributeValues[':ue'].BOOL -ne $UploadsEnabled){throw 'Prepared controls do not match explicit expectations.'}
    & aws dynamodb update-item --profile $Profile --region $c.region --cli-input-json "file://$([IO.Path]::GetFullPath($Request))" --cli-connect-timeout 5 --cli-read-timeout 10
    if($LASTEXITCODE){throw 'CONTROL update failed; state unverified.'}
    $text=& aws dynamodb get-item --profile $Profile --region $c.region --cli-input-json "file://$([IO.Path]::GetFullPath($ReadRequest))" --query 'Item.{admissions:admissionsEnabled.BOOL,uploads:uploadsEnabled.BOOL}' --output json --cli-connect-timeout 5 --cli-read-timeout 10
    if($LASTEXITCODE){throw 'Strong CONTROL verification failed.'}
    $v=($text -join '') | ConvertFrom-Json
    if($v.admissions -isnot [bool] -or $v.uploads -isnot [bool] -or $v.admissions -ne $AdmissionsEnabled -or $v.uploads -ne $UploadsEnabled){throw 'CONTROL flags do not match explicit booleans.'}
    $v
}

function Get-PlaykituAllowance {
    param([Parameter(Mandatory)][string]$ConfigurationFile,[Parameter(Mandatory)][string]$OperationsProfile,
        [Parameter(Mandatory)][string]$DeployProfile,[Parameter(Mandatory)][ValidateRange(1,8)][int]$PlannedAdmissions,
        [Parameter(Mandatory)][ValidatePattern('\A[a-fA-F0-9]{64}\z')][string]$ReviewedIssuerSha256)
    Assert-PlaykituRole $ConfigurationFile $OperationsProfile operations
    Assert-PlaykituRole $ConfigurationFile $DeployProfile deploy
    $c=Read-InfrastructureConfiguration $ConfigurationFile
    $code=& aws lambda get-function-configuration --function-name $c.function --profile $DeployProfile --region $c.region --query CodeSha256 --output text --cli-connect-timeout 5 --cli-read-timeout 10
    if($LASTEXITCODE){throw 'Reviewed issuer hash unavailable.'}
    $actual=[Convert]::ToHexString([Convert]::FromBase64String(($code -join '').Trim()))
    if($actual -ine $ReviewedIssuerSha256){throw 'Live issuer differs from reviewed finite policy.'}
    $text=& aws dynamodb get-item --table-name $c.table --key '{"pk":{"S":"CONTROL"},"sk":{"S":"META"}}' --projection-expression 'admissionsEnabled,uploadsEnabled,#data' --expression-attribute-names '{"#data":"data"}' --consistent-read --profile $OperationsProfile --region $c.region --output json --cli-connect-timeout 5 --cli-read-timeout 10
    if($LASTEXITCODE){throw 'Allowance strong read failed.'}
    $item=(($text -join '') | ConvertFrom-Json -AsHashtable -DateKind String).Item
    $state=$item.data.S | ConvertFrom-Json -AsHashtable -DateKind String
    if($item.admissionsEnabled.BOOL -isnot [bool] -or $item.uploadsEnabled.BOOL -isnot [bool] -or $state.testerDailyAdmissions -isnot [Collections.IDictionary] -or $state.active -isnot [Collections.IDictionary] -or $state.pending -isnot [Collections.IDictionary] -or $state.day -isnot [string] -or $state.month -isnot [string] -or $state.day -notmatch '\A(?:[0-9]{4}-[0-9]{2}-[0-9]{2})?\z' -or $state.month -notmatch '\A(?:[0-9]{4}-[0-9]{2})?\z'){throw 'Invalid control counters.'}
    foreach($n in (@($state.dailyAdmissions,$state.monthlyAdmissions)+@($state.testerDailyAdmissions.Values))){
        if(($n -isnot [int] -and $n -isnot [long]) -or $n -lt 0 -or $n -gt [int]::MaxValue){throw 'Invalid admission counters.'}
    }
    $now=[DateTimeOffset]::UtcNow
    $day=$now.ToString('yyyy-MM-dd');$month=$now.ToString('yyyy-MM')
    $daily=if($state.day -ceq $day){$state.dailyAdmissions}else{0}
    $monthly=if($state.month -ceq $month){$state.monthlyAdmissions}else{0}
    $tester=0
    if($state.day -ceq $day){foreach($n in $state.testerDailyAdmissions.Values){$tester=[Math]::Max($tester,$n)}}
    $available=(!$item.admissionsEnabled.BOOL -and !$item.uploadsEnabled.BOOL -and $state.active.Count -eq 0 -and $state.pending.Count -eq 0 -and $PlannedAdmissions -le 8-$daily -and $PlannedAdmissions -le 50-$monthly -and $PlannedAdmissions -le 8-$tester)
    [pscustomobject]@{AllowanceAvailable=$available;GlobalDailyRemaining=[Math]::Max(0,8-$daily);GlobalMonthlyRemaining=[Math]::Max(0,50-$monthly);ConservativeTesterRemaining=[Math]::Max(0,8-$tester);PlannedAdmissions=$PlannedAdmissions;Meaning='Read-only conservative upper bound on tester usage; no identities or quota reset.'}
}
