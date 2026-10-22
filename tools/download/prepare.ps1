. "$PSScriptRoot/../../backend/issuer/environment.ps1"
& "$PSScriptRoot/../../backend/issuer/prepare.ps1"
foreach($project in 'src/Retrieval.csproj','tests/Retrieval.Tests.csproj'){
 & "$taskSdk/dotnet.exe" restore (Join-Path $PSScriptRoot $project) --locked-mode
 if($LASTEXITCODE){throw 'Download locked restore failed.'}
}
