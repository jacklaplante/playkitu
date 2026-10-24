param([Parameter(Mandatory)][string]$Deployment,[Parameter(Mandatory)][string]$Profile,
    [string]$Output=(Join-Path $PSScriptRoot 'private/library'),[string]$NativeRuntime='', [switch]$NoOpen)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../backend/issuer/environment.ps1"
$dll=Join-Path $PSScriptRoot 'src/bin/Release/net10.0/Playkitu.Retrieval.dll'
if(!(Test-Path -LiteralPath $dll)){throw 'Run tools/download/prepare.ps1 and test.ps1 first.'}
if(![IO.Path]::IsPathFullyQualified($Deployment) -or ![IO.Path]::IsPathFullyQualified($Output)){throw 'Use absolute deployment and download paths.'}
$start=[Diagnostics.ProcessStartInfo]::new("$taskSdk/dotnet.exe")
$start.UseShellExecute=$false
foreach($key in @($start.Environment.Keys)){
    if($key -like 'AWS_*' -and $key -notin @('AWS_CONFIG_FILE','AWS_SHARED_CREDENTIALS_FILE','AWS_LOGIN_CACHE_DIRECTORY','AWS_EC2_METADATA_DISABLED')){$start.Environment.Remove($key)|Out-Null}
}
foreach($arg in @($dll,'browse','--deployment',$Deployment,'--profile',$Profile,'--output',$Output,'--tool-root',$PSScriptRoot)){$start.ArgumentList.Add($arg)}
if($NativeRuntime){$start.ArgumentList.Add('--native-runtime');$start.ArgumentList.Add($NativeRuntime)}
if($NoOpen){$start.ArgumentList.Add('--no-open');$start.ArgumentList.Add('true')}
$process=[Diagnostics.Process]::Start($start)
try {$process.WaitForExit();exit $process.ExitCode}
finally {if(!$process.HasExited){$process.Kill($true)};$process.Dispose()}
