param([string]$EvidenceRoot)
. "$PSScriptRoot/common.ps1"
if (!$EvidenceRoot) { $EvidenceRoot=Join-Path $PSScriptRoot "private/C1-external-$([guid]::NewGuid().ToString('N'))" }
$EvidenceRoot=[IO.Path]::GetFullPath($EvidenceRoot)
$allowed=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'private'))+[IO.Path]::DirectorySeparatorChar
if(!$EvidenceRoot.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $EvidenceRoot) -notlike 'C1-external-*') {throw 'Task-owned C1 external evidence directory required'}
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null
$gst=Join-Path $Private 'gstreamer/{app}'
$runtime=Join-Path $Private 'runtime'
$tool=Get-NativeToolchain
$envs=$tool.Environment
$envs.PATH="$gst/bin;"+$envs.PATH
$envs.PKG_CONFIG_PATH="$gst/lib/pkgconfig"; $envs.PKG_CONFIG_LIBDIR="$gst/lib/pkgconfig"
$build=Join-Path $EvidenceRoot 'build'
$r=Invoke-BoundedProcess $tool.CMake @('-S',$PSScriptRoot,'-B',$build,'-G','NMake Makefiles','-DCMAKE_BUILD_TYPE=Release',"-DPKG_CONFIG_EXECUTABLE=$gst/bin/pkg-config.exe") $PSScriptRoot (Join-Path $EvidenceRoot 'configure-current.log') 60 $envs
if($r.ExitCode){throw 'External input configure failed'}
$r=Invoke-BoundedProcess $tool.CMake @('--build',$build,'--target','native-external-input-tests','native-synthetic-tests') $PSScriptRoot (Join-Path $EvidenceRoot 'build-current.log') 120 $envs
if($r.ExitCode){throw 'External input target build failed'}
$plugins=Join-Path $EvidenceRoot 'plugins'
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Get-ChildItem "$runtime/plugins" -File | Where-Object Name -ne 'gstkvssink.dll' | Copy-Item -Destination $plugins
foreach($name in @('app','audioconvert')) {Copy-Item -LiteralPath "$gst/lib/gstreamer-1.0/gst$name.dll" $plugins}
Copy-Item "$Private/build-producer-flat/offline/gstkvssink.dll" $plugins
$results=@()
foreach($case in @('success','overload','no-input','stop','generated-regression')) {
    $dir=Join-Path $EvidenceRoot ($case+'-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $runEnv=Get-PrivateRuntimeEnvironment $runtime $dir -Region 'eu-west-1'
    $runEnv.PATH="$build;$gst/bin;"+$runEnv.PATH
    $runEnv.GST_PLUGIN_SYSTEM_PATH=$plugins; $runEnv.GST_PLUGIN_SYSTEM_PATH_1_0=$plugins
    $exe=if($case -eq 'generated-regression') {'native-synthetic-tests.exe'}else{'native-external-input-tests.exe'}
    $argument=if($case -eq 'generated-regression') {'success'}else{$case}
    $r=Invoke-BoundedProcess (Join-Path $build $exe) @("$plugins/gstkvssink.dll","$dir/media.mkv","$runtime/certs/cert.pem",$argument) $dir "$dir/test.log" 30 $runEnv -MinimalEnvironment
    $log=Get-Content $r.Log -Raw
    foreach($secret in @('OFFLINEONLYACCESS','OFFLINEONLYSECRET','OFFLINEONLYSESSION')) {if($log.Contains($secret)){throw 'Fake credential leaked into test evidence'}}
    if($r.ExitCode -or $log -notmatch 'PASS checks=(\d+)'){Write-Output $r.Log;Get-Content $r.Log -TotalCount 6;throw "External input case failed: $case"}
    $results+=@{Case=$case;Checks=[int]$Matches[1];Process=$r;Media=$(if(Test-Path "$dir/media.mkv"){"$dir/media.mkv"}else{$null})}
    Write-Output "Passed $case ($($r.Seconds)s): $dir"
}
@{Passed=$true;Cases=$results;OfflinePluginSha256=(Get-FileHash "$plugins/gstkvssink.dll").Hash;NativeDllSha256=(Get-FileHash "$build/PlaytestRecordingNativeProof.dll").Hash;NoCloud=$true;NoRealCapture=$true;AcceptedRuntimeNotPublished=$true}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $EvidenceRoot 'external-tests.json')
Write-Output (Join-Path $EvidenceRoot 'external-tests.json')
