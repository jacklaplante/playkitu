param([switch]$GeneratedMedia)
. "$PSScriptRoot/common.ps1"
$evidence=Join-Path $Private ('tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $evidence -Force | Out-Null
try {
 $runtime=Join-Path $Private 'runtime';$harness=Join-Path $Private 'build-harness'
 $envs=Get-PrivateRuntimeEnvironment $runtime $evidence -Region 'eu-west-1'
 $envs.PATH="$harness;"+$envs.PATH
 $dll="$runtime/bin/PlaytestRecordingNativeProof.dll";$missing="$evidence/missing-public-ca.pem"
 $results=@()
 foreach($case in @(
  @{name='region';exe='native-region-tests';args=@($missing)},
  @{name='config';exe='native-media-config-tests';args=@($dll)},
  @{name='clock';exe='native-clock-tests';args=@('unit')},
  @{name='pacing';exe='native-sender-pacing-tests';args=@()},
  @{name='lifecycle';exe='native-synthetic-lifecycle-tests';args=@($missing)})) {
  $r=Invoke-BoundedProcess "$harness/$($case.exe).exe" $case.args $evidence "$evidence/$($case.name).log" 60 $envs -MinimalEnvironment
  if($r.ExitCode){throw "Native case failed: $($case.name); $($r.Log)"};$results+=@{name=$case.name;exitCode=0;log=$r.Log}
 }
 $shell=(Get-Process -Id $PID).Path
 foreach($test in @('test-patches.ps1','test-credentials.ps1')){
  $r=Invoke-BoundedProcess $shell @('-NoProfile','-File',"$PSScriptRoot/$test") $evidence "$evidence/$test.log" 120
  if($r.ExitCode){throw "Native script failed: $test; $($r.Log)"};$results+=@{name=$test;exitCode=0;log=$r.Log}
 }
 # A negative export DLL reproduces a legacy loader contract. Its dependencies
 # are the exact current production closure; it is never packaged or published.
 $legacy="$evidence/legacy/bin";New-Item -ItemType Directory $legacy -Force | Out-Null
 Get-ChildItem "$runtime/bin" -File | Where-Object Name -ne 'PlaytestRecordingNativeProof.dll' | Copy-Item -Destination $legacy
 Copy-Item "$harness/legacy/PlaytestRecordingNativeProof.dll" $legacy
 $r=Invoke-BoundedProcess (Get-Command dotnet).Source @('run','--project',"$PSScriptRoot/tests/ManagedBoundary/ManagedBoundary.csproj",'--', $runtime,"$evidence/legacy") $evidence "$evidence/managed-capability.log" 180 $envs
 if($r.ExitCode){throw "Managed CREATED-only loader check failed: $($r.Log)"}
 $results+=@{name='managed-capability';exitCode=0;log=$r.Log}
 if($GeneratedMedia) {
  $plugins="$evidence/plugins";New-Item -ItemType Directory $plugins | Out-Null
  Get-ChildItem "$runtime/plugins" -File | Where-Object Name -ne 'gstkvssink.dll' | Copy-Item -Destination $plugins
  Copy-Item "$Private/build-producer-flat/offline/gstkvssink.dll" $plugins
  foreach($case in @('configured','retirement','retirement-refused','no-input')) {
   $dir="$evidence/$case";New-Item -ItemType Directory $dir | Out-Null
   $runEnv=Get-PrivateRuntimeEnvironment $runtime $dir -Region 'eu-west-1';$runEnv.PATH="$harness;"+$runEnv.PATH
   $runEnv.GST_PLUGIN_SYSTEM_PATH=$runEnv.GST_PLUGIN_SYSTEM_PATH_1_0=$plugins
   $r=Invoke-BoundedProcess "$harness/native-external-input-tests.exe" @("$plugins/gstkvssink.dll","$dir/media.mkv","$runtime/certs/cert.pem",$case) $dir "$dir/test.log" 45 $runEnv -MinimalEnvironment
   if($r.ExitCode){throw "Generated SDK case failed: $case; $($r.Log)"};$results+=@{name=$case;exitCode=0;log=$r.Log}
  }
 }
 @{passed=$true;generatedMedia=[bool]$GeneratedMedia;cases=$results;noCloud=$true;noDevices=$true} | ConvertTo-Json -Depth 7 | Set-Content "$evidence/result.json"
 Write-Output "PASS native_suite_cases=$($results.Count) evidence=$evidence"
}catch{Write-Error "$_ Evidence: $evidence" -ErrorAction Continue;exit 1}
