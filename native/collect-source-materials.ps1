param([Parameter(Mandatory)][string]$Output)
. "$PSScriptRoot/common.ps1"
$Output=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $Output){throw 'Fresh source-material directory required'}
$nativePrefix=[IO.Path]::GetFullPath($PSScriptRoot)+[IO.Path]::DirectorySeparatorChar
$privatePrefix=[IO.Path]::GetFullPath($Private)+[IO.Path]::DirectorySeparatorChar
if($Output.StartsWith($nativePrefix,[StringComparison]::OrdinalIgnoreCase) -and !$Output.StartsWith($privatePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Output inside native sources must be under ignored private/'}
$lock=Get-Content "$PSScriptRoot/dependencies.json" -Raw | ConvertFrom-Json
# Only pinned source archives; no private runtime, secrets, logs or media.
$archives=@($lock.artifacts | Where-Object file -in @('producer.zip','producer-c.zip','pic.zip','log4cplus-current.zip','threadpool.zip'))
foreach($entry in $archives){if((Get-FileHash "$Private/downloads/$($entry.file)").Hash -ne $entry.sha256){throw 'Missing or changed source archive'}}
New-Item -ItemType Directory "$Output/native","$Output/upstream" -Force | Out-Null
$ignored=@('private','bin','obj')
foreach($item in Get-ChildItem $PSScriptRoot){if($item.Name -notin $ignored){Copy-Item -LiteralPath $item.FullName -Destination "$Output/native" -Recurse}}
# Remove build outputs nested under the copied managed test project as well.
foreach($name in @('bin','obj')){$target=[IO.Path]::GetFullPath("$Output/native/tests/ManagedBoundary/$name");$prefix=$Output+[IO.Path]::DirectorySeparatorChar;if(!$target.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe source-material cleanup'};if(Test-Path $target){Remove-Item -LiteralPath $target -Recurse}}
foreach($entry in $archives){Copy-Item "$Private/downloads/$($entry.file)" "$Output/upstream"}
foreach($folder in @('Client','Native','Producer')){
 $target="$Output/packages/com.playkitu.recording/$folder";New-Item -ItemType Directory $target -Force | Out-Null
 Get-ChildItem "$PSScriptRoot/../packages/com.playkitu.recording/$folder" -File | Where-Object Extension -eq '.cs' | Copy-Item -Destination $target
}
@{binaryReleaseApproved=$false;missing='Exact GStreamer/dependency corresponding sources, build recipes and binary redistribution/security gates in REDISTRIBUTION.md';archives=$archives} | ConvertTo-Json -Depth 5 | Set-Content "$Output/materials.json"
Get-ChildItem $Output -File -Recurse | ForEach-Object {@{path=$_.FullName.Substring($Output.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash}} | ConvertTo-Json | Set-Content "$Output/inventory.json"
Write-Output "Source materials collected: $Output (binary release remains held)"
