. "$PSScriptRoot/environment.ps1"
& "$taskSdk/dotnet.exe" "$PSScriptRoot/src/bin/Release/net10.0/Playreel.Issuer.dll" --seed-control
if($LASTEXITCODE){throw 'Build/test the issuer before preparing its canonical seed.'}
