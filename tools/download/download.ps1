param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../native/common.ps1"
. "$PSScriptRoot/../../backend/issuer/environment.ps1"
$toolRoot=$PSScriptRoot
$evidence=Join-Path $toolRoot ('private/runs/'+[guid]::NewGuid().ToString('N'))
New-Item $evidence -ItemType Directory -Force | Out-Null
$dll=Join-Path $toolRoot 'src/bin/Release/net10.0/Playkitu.Retrieval.dll'
if(!(Test-Path $dll)){throw 'Run tools/download/test.ps1 first.'}
$child=@{DOTNET_ROOT=$taskSdk;DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME;DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_GENERATE_ASPNET_CERTIFICATE='false';AWS_EC2_METADATA_DISABLED='true';PATH=$env:PATH}
if($Arguments.Count -and $Arguments[0] -in 'list','fetch','metadata'){
 foreach($key in 'AWS_CONFIG_FILE','AWS_SHARED_CREDENTIALS_FILE','AWS_LOGIN_CACHE_DIRECTORY'){
  $value=[Environment]::GetEnvironmentVariable($key)
  if($value){$child[$key]=$value}
 }
}
$result=Invoke-BoundedProcess "$taskSdk/dotnet.exe" (@($dll)+$Arguments+@('--tool-root',$toolRoot)) $evidence "$evidence/result.log" 310 $child -MinimalEnvironment
Get-Content $result.Log
Write-Output "Download evidence: $evidence"
exit $result.ExitCode
