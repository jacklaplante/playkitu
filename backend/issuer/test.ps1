. "$PSScriptRoot/environment.ps1"
$evidence=Join-Path $taskPrivate ('tests/'+[guid]::NewGuid().ToString('N'))
New-Item $evidence -ItemType Directory -Force | Out-Null
$env:AWS_CONFIG_FILE=Join-Path $evidence 'empty-config'
$env:AWS_SHARED_CREDENTIALS_FILE=Join-Path $evidence 'empty-credentials'
'' | Set-Content $env:AWS_CONFIG_FILE
'' | Set-Content $env:AWS_SHARED_CREDENTIALS_FILE
$env:AWS_ACCESS_KEY_ID='OFFLINE_TEST_ONLY';$env:AWS_SECRET_ACCESS_KEY='OFFLINE_TEST_ONLY';$env:AWS_SESSION_TOKEN='OFFLINE_TEST_ONLY'
$env:AWS_PROFILE='nonexistent-offline-fixture'
# Endpoint overrides alter presigned URL semantics even without sending HTTP.
# Preserve normal HTTPS signing while preventing outbound traffic independently.
foreach($key in @(Get-ChildItem Env: | Where-Object Name -like 'AWS_ENDPOINT_URL*' | ForEach-Object Name)){
    Remove-Item "Env:$key"
}
$env:HTTP_PROXY='http://127.0.0.1:9';$env:HTTPS_PROXY='http://127.0.0.1:9';$env:NO_PROXY=''

& "$taskSdk/dotnet.exe" test "$PSScriptRoot/tests/Issuer.Tests.csproj" --no-restore -c Release --logger 'trx;LogFileName=issuer.trx' --results-directory $evidence -v minimal
if($LASTEXITCODE){throw "Offline issuer tests failed: $evidence"}
