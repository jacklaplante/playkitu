param([ValidateRange(0,8)][int]$VideoStreams=4,[ValidateSet('pooled','onDemand')][string]$StreamMode='pooled')
. "$PSScriptRoot/environment.ps1"
& "$taskSdk/dotnet.exe" "$PSScriptRoot/src/bin/Release/net10.0/Playkitu.Issuer.dll" --seed-control --video-streams $VideoStreams --stream-mode $StreamMode
if($LASTEXITCODE){throw 'Build/test the issuer before preparing its canonical seed.'}
