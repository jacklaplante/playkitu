param([string]$ProbeFixture='')
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../backend/issuer/environment.ps1"
$evidence=Join-Path $PSScriptRoot ('private/tests/'+[guid]::NewGuid().ToString('N'))
New-Item $evidence -ItemType Directory -Force | Out-Null
$env:AWS_CONFIG_FILE=Join-Path $evidence 'empty-config'
$env:AWS_SHARED_CREDENTIALS_FILE=Join-Path $evidence 'empty-credentials'
$env:AWS_LOGIN_CACHE_DIRECTORY=Join-Path $evidence 'empty-login-cache'
'' | Set-Content $env:AWS_CONFIG_FILE
'' | Set-Content $env:AWS_SHARED_CREDENTIALS_FILE
New-Item $env:AWS_LOGIN_CACHE_DIRECTORY -ItemType Directory | Out-Null
$env:SBREC_RETRIEVAL_TEST_ROOT=$evidence
$env:SBREC_RETRIEVAL_TOOL_ROOT=$PSScriptRoot
if($ProbeFixture){$env:SBREC_RETRIEVAL_PROBE_FIXTURE=[IO.Path]::GetFullPath($ProbeFixture)}else{Remove-Item Env:SBREC_RETRIEVAL_PROBE_FIXTURE -ErrorAction SilentlyContinue}
$env:AWS_ACCESS_KEY_ID='OFFLINE_TEST_ONLY';$env:AWS_SECRET_ACCESS_KEY='OFFLINE_TEST_ONLY';$env:AWS_SESSION_TOKEN='OFFLINE_TEST_ONLY'
$env:AWS_PROFILE='nonexistent-offline-fixture';$env:AWS_ENDPOINT_URL='http://127.0.0.1:9'
& "$taskSdk/dotnet.exe" test "$PSScriptRoot/tests/Retrieval.Tests.csproj" --no-restore -c Release --logger 'trx;LogFileName=download.trx' --results-directory $evidence -v minimal *> "$evidence/test.log"
$code=$LASTEXITCODE
Get-Content "$evidence/test.log" -Tail 12
if($code){throw "Offline download tests failed: $evidence"}
foreach($command in @(@('inspect','--directory',"$evidence/cli-bundle"),@('map','--directory',"$evidence/cli-bundle",'--elapsed-ns','0'))){
 & pwsh -NoProfile -File "$PSScriptRoot/download.ps1" @command *> "$evidence/$($command[0]).log"
 $expected=if($command[0] -eq 'map'){3}else{0}
 if($LASTEXITCODE -ne $expected){throw 'Local CLI acceptance changed.'}
}
Write-Output "Offline download checks passed: $evidence"
