. "$PSScriptRoot/environment.ps1"
$taskZip = Join-Path $taskPrivate 'dotnet-sdk-10.0.402-win-x64.zip'
$taskHash = 'ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd'
New-Item -ItemType Directory -Force -Path $taskPrivate | Out-Null
if (!(Test-Path -LiteralPath (Join-Path $taskSdk 'dotnet.exe'))) {
if (!(Test-Path -LiteralPath $taskZip)) {
    Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-win-x64.zip' -OutFile $taskZip
}
if ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA512).Hash -ne $taskHash) { throw 'SDK SHA512 mismatch.' }
if (!(Test-Path -LiteralPath (Join-Path $taskSdk 'dotnet.exe'))) { Expand-Archive -LiteralPath $taskZip -DestinationPath $taskSdk }
}
Push-Location $taskRoot
try {
    & "$taskSdk/dotnet.exe" --version
    if ($LASTEXITCODE -ne 0) { throw 'Private SDK failed.' }
    & "$taskSdk/dotnet.exe" restore src/Issuer.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Issuer restore failed.' }
    & "$taskSdk/dotnet.exe" restore tests/Issuer.Tests.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
} finally { Pop-Location }
