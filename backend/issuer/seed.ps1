param([ValidateRange(1,8)][int]$VideoStreams=4)
. "$PSScriptRoot/environment.ps1"
& "$taskSdk/dotnet.exe" "$PSScriptRoot/src/bin/Release/net10.0/Playkitu.Issuer.dll" --seed-control --video-streams $VideoStreams
if($LASTEXITCODE){throw 'Build/test the issuer before preparing its canonical seed.'}
