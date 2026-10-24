# Pinned source/archive patch regression; no runtime or .git checkout is required.
. "$PSScriptRoot/common.ps1"
$evidence=Join-Path $Private ('patch-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $evidence -Force | Out-Null
$shell=(Get-Process -Id $PID).Path
# Fixture reconstruction must actually reverse patches, even when these tests
# live below a Git checkout. A zero exit code alone can mean every file was skipped.
$fixtureGitDirectory = & git -C $PSScriptRoot rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -ne 0) { $fixtureGitDirectory = $Private }
$cases=@()
try {
 foreach($case in @('fresh-clone','fresh','current','crlf','tamper-cpp','tamper-c','mixed-cpp','invalid-patch','wrong-root')) {
  $root=Join-Path $evidence $case
  New-Item -ItemType Directory "$root/private/source" -Force | Out-Null
  Copy-Item "$PSScriptRoot/common.ps1","$PSScriptRoot/apply-credentials-patch.ps1" $root
  Copy-Item "$PSScriptRoot/patches" $root -Recurse
  if($case -eq 'fresh-clone') {
   & git init --quiet $root
   if($LASTEXITCODE){throw 'Fresh-clone fixture initialization failed'}
  }
  foreach($pair in @(@('cpp','kvssink-memory-provider'),@('c','producer-c-private-logging'))) {
   $sdk=$pair[0];$name=$pair[1];$manifest=Get-Content "$root/patches/$name.json" -Raw | ConvertFrom-Json
   foreach($file in $manifest.files){$target="$root/private/source/$sdk/$($file.file)";New-Item -ItemType Directory ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null;Copy-Item "$Private/source/$sdk/$($file.file)" $target}
   if($case -in 'fresh','fresh-clone','crlf','invalid-patch'){
    & git -C $fixtureGitDirectory apply --reverse --ignore-space-change --unsafe-paths "--directory=$root/private/source/$sdk" "$root/patches/$name.patch"
    if($LASTEXITCODE){throw 'Fixture base reconstruction failed'}
    foreach($file in $manifest.files){
     $target="$root/private/source/$sdk/$($file.file)";$hash=$null
     if(Test-Path -LiteralPath $target){
      $text=[IO.File]::ReadAllText($target).Replace("`r`n","`n")
      $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
     }
     if($hash -ne $file.baseSha256){throw "Fixture did not reconstruct pinned base: $($file.file)"}
    }
   }
  }
  if($case -eq 'crlf'){Get-ChildItem "$root/private/source" -File -Recurse | ForEach-Object {$text=[IO.File]::ReadAllText($_.FullName).Replace("`r`n","`n").Replace("`n","`r`n");[IO.File]::WriteAllText($_.FullName,$text,[Text.UTF8Encoding]::new($false))}}
  if($case -eq 'tamper-cpp'){[IO.File]::AppendAllText("$root/private/source/cpp/src/gstreamer/gstkvssink.cpp","`n// unexpected`n")}
  if($case -eq 'tamper-c'){[IO.File]::AppendAllText("$root/private/source/c/src/source/Response.c","`n// unexpected`n")}
  if($case -eq 'mixed-cpp'){
   $patch=[IO.File]::ReadAllText("$root/patches/kvssink-memory-provider.patch");$start=$patch.IndexOf('diff --git a/CMakeLists.txt');$end=$patch.IndexOf('diff --git ',$start+1)
   $part=if($end -lt 0){$patch.Substring($start)}else{$patch.Substring($start,$end-$start)}
   [IO.File]::WriteAllText("$root/one.patch",$part)
   & git -C $fixtureGitDirectory apply --reverse --ignore-space-change --unsafe-paths "--directory=$root/private/source/cpp" "$root/one.patch"
   if($LASTEXITCODE){throw 'Mixed fixture construction failed'}
  }
  if($case -eq 'invalid-patch'){Set-Content "$root/patches/producer-c-private-logging.patch" 'invalid patch'}
  $before=@(Get-ChildItem "$root/private/source" -File -Recurse | ForEach-Object {(Get-FileHash $_.FullName).Hash}) -join ','
  $arguments=@('-NoProfile','-File',"$root/apply-credentials-patch.ps1")
  if($case -eq 'wrong-root'){$arguments+=@('-SourceRoot',"$root/private/source/c")}
  $r=Invoke-BoundedProcess $shell $arguments $root "$root/result.log" 30
  $expected=if($case -in 'fresh','fresh-clone','current','crlf'){0}else{1}
  if($r.ExitCode -ne $expected){throw "Patch regression failed: $case"}
  if($expected){$after=@(Get-ChildItem "$root/private/source" -File -Recurse | ForEach-Object {(Get-FileHash $_.FullName).Hash}) -join ',';if($before -ne $after){throw 'Rejected patch mutated source'}}
  $cases+=@{case=$case;exitCode=$r.ExitCode}
 }
 @{passed=$true;caseCount=$cases.Count;cases=$cases;sourceArchiveWithoutGit=$true} | ConvertTo-Json -Depth 5 | Set-Content "$evidence/result.json"
 Write-Output "PASS patch_cases=$($cases.Count) evidence=$evidence"
}catch{Write-Error "$_ Evidence: $evidence" -ErrorAction Continue;exit 1}
