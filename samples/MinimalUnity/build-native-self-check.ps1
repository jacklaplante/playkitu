param([Parameter(Mandatory)][string]$Output,[string]$Runtime,[string]$Unity)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath($PSScriptRoot)
$repo=[IO.Path]::GetFullPath((Join-Path $project '../..'))
if(![IO.Path]::IsPathRooted($Output)){throw 'Fresh absolute output required'}
$Output=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $Output){throw 'Fresh sample output required'}
if(!$Runtime){$Runtime=Join-Path $repo 'native/private/runtime'}
$Runtime=[IO.Path]::GetFullPath($Runtime).TrimEnd('\','/')
if(!$Unity){
 $version=((Get-Content "$project/ProjectSettings/ProjectVersion.txt" | Select-String '^m_EditorVersion:').Line.Split(':')[1]).Trim()
 $Unity=Join-Path $env:ProgramFiles "Unity/Hub/Editor/$version/Editor/Unity.exe"
}
if(!(Test-Path -LiteralPath $Unity) -or !(Test-Path "$Runtime/bin/PlaytestRecordingNativeProof.dll")){throw 'Unity version or prepared native runtime missing'}
if(Test-Path "$project/Temp/UnityLockfile"){
 # A compiler-error exit can leave the file behind. Only an exclusive open proves
 # it is stale; never delete it or bypass a lock held by a running sample Editor.
 try {$lock=[IO.File]::Open("$project/Temp/UnityLockfile",[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$lock.Dispose()}
 catch {throw 'Close this sample Editor before batch building'}
}
$external=$Output+'-external'
if(Test-Path $external){throw 'Fresh external evidence required'}
New-Item -ItemType Directory $external | Out-Null
# Restore all sample settings bytes after the owned Editor exits, including files
# Unity may create during its first build. Never restore underneath a running Editor.
$before=@{}
foreach($file in Get-ChildItem "$project/ProjectSettings" -File -Recurse){$before[$file.FullName]=[IO.File]::ReadAllBytes($file.FullName)}
$special=@("$project/UserSettings/EditorUserSettings.asset","$project/Library/EditorUserBuildSettings.asset")
foreach($file in $special){$before[[IO.Path]::GetFullPath($file)]=if(Test-Path $file){[IO.File]::ReadAllBytes($file)}else{$null}}
$settingsHashes=@(foreach($file in $before.Keys){@{path=$file.Substring($project.Length+1);sha256=if($null -ne $before[$file]){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($before[$file]))}else{$null}}})
$settingsHashes | ConvertTo-Json | Set-Content "$external/settings-before.json"
$child=$null;$exitCode=$null
try {
 $args=@('-batchmode','-nographics','-quit','-buildTarget','Win64','-job-worker-count','2','-projectPath',$project,
  '-executeMethod','Playreel.Sample.Editor.NativeSampleBuild.Build','-playreelOutput',$Output,'-playreelRuntime',$Runtime,'-logFile',"$external/unity-build.log")
 $info=[Diagnostics.ProcessStartInfo]::new($Unity)
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
 $info.WorkingDirectory=$project
 foreach($argument in $args){$info.ArgumentList.Add($argument)}
 $child=[Diagnostics.Process]::Start($info)
 if(!$child.WaitForExit(1200000)){$child.Kill($true);$child.WaitForExit();throw 'Minimal sample build exceeded twenty-minute deadline'}
 $exitCode=$child.ExitCode
} finally {
 if($child -and !$child.HasExited){$child.Kill($true);$child.WaitForExit()}
 foreach($file in Get-ChildItem "$project/ProjectSettings" -File -Recurse){
  if(!$before.ContainsKey($file.FullName)){
   $target=[IO.Path]::GetFullPath($file.FullName);$prefix=[IO.Path]::GetFullPath("$project/ProjectSettings")+[IO.Path]::DirectorySeparatorChar
   if(!$target.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe settings restoration target'}
   Remove-Item -LiteralPath $target
  }
 }
 foreach($file in $before.Keys){
  if($null -ne $before[$file]){[IO.File]::WriteAllBytes($file,$before[$file])}
  elseif(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file}
 }
 $restored=$true
 foreach($file in $before.Keys){if($null -eq $before[$file]){if(Test-Path $file){$restored=$false}}elseif((Get-FileHash $file).Hash -ne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($before[$file]))){$restored=$false}}
 @{exitCode=$exitCode;settingsRestored=$restored;output=$Output} | ConvertTo-Json | Set-Content "$external/process-result.json"
}
if(!$restored){throw 'Sample settings restoration mismatch'}
$stream=[IO.File]::Open("$external/unity-build.log",[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
$reader=[IO.StreamReader]::new($stream)
try {$log=$reader.ReadToEnd()} finally {$reader.Dispose()}
if($exitCode -ne 0 -or $log -match 'error CS[0-9]+|Scripts have compiler errors|Aborting batchmode due to fatal error|another Unity instance is running'){throw "Unity compile/build failed; evidence: $external"}
$result=Get-Content "$Output/build-result.json" -Raw | ConvertFrom-Json
if($result.result -ne 'Succeeded' -or $result.errors -ne 0 -or !$result.runtimeIdentical -or !$result.backendRestored -or $result.error){throw 'Minimal sample build receipt failed'}
Write-Output "Minimal Windows64 IL2CPP sample built; settings restored. Evidence: $external"
