# Focused lifecycle regression and prior-bug negative control. Both use a missing
# public-CA sentinel that stops any worker before GStreamer/plugin initialization.
. "$PSScriptRoot/common.ps1"
$toolRoot=$PSScriptRoot
$evidence=Join-Path $Private "lifecycle-tests/$([guid]::NewGuid())"
New-Item -ItemType Directory "$evidence/empty-plugins" -Force | Out-Null
try {
    $runtime=Join-Path $Private 'runtime'
    $envs=Get-PrivateRuntimeEnvironment $runtime $evidence -Region 'eu-west-1'
    $envs.GST_PLUGIN_SYSTEM_PATH=$envs.GST_PLUGIN_SYSTEM_PATH_1_0="$evidence/empty-plugins"
    $sentinel=Join-Path $evidence 'missing-public-ca.pem'
    $r=Invoke-BoundedProcess "$Private/build-harness/native-synthetic-lifecycle-tests.exe" @($sentinel) $evidence "$evidence/lifecycle.log" 30 $envs -MinimalEnvironment
    if($r.ExitCode -or (Get-Content $r.Log -Raw) -notmatch 'PASS lifecycle_checks=(\d+) release_wins=256 start_wins=256') { throw "Lifecycle regression failed: $($r.Log)" }
    $checks=[int]$Matches[1]

    # Prove the regression detects the prior defect, in an isolated source copy.
    # Only the created-run retirement is omitted; real sources/package untouched.
    $negative=Join-Path $evidence 'negative'
    New-Item -ItemType Directory $negative | Out-Null
    $source=[IO.File]::ReadAllText("$toolRoot/native-synthetic-runner.cpp")
    if(([regex]::Matches($source,'active->retire_created\(\);')).Count -ne 1) { throw 'Negative-control source anchor changed' }
    [IO.File]::WriteAllText("$negative/negative-runner.cpp",$source.Replace('active->retire_created();','(void)0;'))
    $test=[IO.File]::ReadAllText("$toolRoot/tests/native-synthetic-lifecycle.cpp")
    [IO.File]::WriteAllText("$negative/negative-test.cpp",$test.Replace('"../native-synthetic-runner.cpp"','"negative-runner.cpp"'))
    $nativePath=$toolRoot.Replace('\','/')
    $project=@"
cmake_minimum_required(VERSION 3.20)
project(LifecycleNegative LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)
find_package(PkgConfig REQUIRED)
pkg_check_modules(GST REQUIRED IMPORTED_TARGET gstreamer-1.0)
pkg_check_modules(GST_APP REQUIRED IMPORTED_TARGET gstreamer-app-1.0)
add_executable(lifecycle-negative negative-test.cpp "$nativePath/native-synthetic-media.cpp" "$nativePath/native-media-clock.cpp")
target_include_directories(lifecycle-negative PRIVATE "$nativePath")
target_compile_definitions(lifecycle-negative PRIVATE NOMINMAX)
target_link_libraries(lifecycle-negative PRIVATE PkgConfig::GST PkgConfig::GST_APP)
"@
    [IO.File]::WriteAllText("$negative/CMakeLists.txt",$project)
    $tool=Get-NativeToolchain
    $gst=Join-Path $Private 'gstreamer/{app}'
    $buildEnv=$tool.Environment
    $buildEnv.PATH="$gst/bin;"+$buildEnv.PATH
    $buildEnv.PKG_CONFIG_PATH=$buildEnv.PKG_CONFIG_LIBDIR="$gst/lib/pkgconfig"
    $r=Invoke-BoundedProcess $tool.CMake @('-S',$negative,'-B',"$negative/build",'-G','NMake Makefiles','-DCMAKE_BUILD_TYPE=Release',"-DPKG_CONFIG_EXECUTABLE=$gst/bin/pkg-config.exe") $evidence "$evidence/negative-configure.log" 60 $buildEnv
    if($r.ExitCode) { throw 'Negative-control configure failed' }
    $r=Invoke-BoundedProcess $tool.CMake @('--build',"$negative/build") $evidence "$evidence/negative-build.log" 60 $buildEnv
    if($r.ExitCode) { throw 'Negative-control build failed' }
    $r=Invoke-BoundedProcess "$negative/build/lifecycle-negative.exe" @($sentinel) $evidence "$evidence/negative.log" 30 $envs -MinimalEnvironment
    if($r.ExitCode -ne 1 -or (Get-Content $r.Log -Raw) -notmatch 'expression=started==RUN_BUSY') { throw 'Negative control did not reproduce the retained-start defect' }
    foreach($file in @('lifecycle.log','negative.log')) {
        if((Get-Content "$evidence/$file" -Raw) -match 'OFFLINEONLY') { throw 'Lifecycle diagnostics leaked a fixture marker' }
    }
    @{Passed=$true;Checks=$checks;ReleaseWins=256;StartWins=256;NegativeControlExitCode=$r.ExitCode;
      NoPluginInitialization=$true;NoSockets=$true;
      RunnerSourceSHA256=(Get-FileHash "$toolRoot/native-synthetic-runner.cpp").Hash;
      UnitTestSHA256=(Get-FileHash "$toolRoot/tests/native-synthetic-lifecycle.cpp").Hash;
      RuntimeDLLSHA256=(Get-FileHash "$runtime/bin/PlaytestRecordingNativeProof.dll").Hash} |
      ConvertTo-Json | Set-Content "$evidence/result.json"
    Write-Output "Passed 512 lifecycle interleavings and prior-bug negative control. Evidence: $evidence"
    exit 0
} catch { Write-Error "$_ Evidence: $evidence" -ErrorAction Continue; exit 1 }
