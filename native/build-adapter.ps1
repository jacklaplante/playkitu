# Rebuild only shared proof code using the already-reviewed private dependency cache.
. "$PSScriptRoot/common.ps1"
try {
    $gst = Join-Path $Private 'gstreamer/{app}'
    $tool = Get-NativeToolchain
    $envs = $tool.Environment
    $envs.PATH = "$gst/bin;" + $envs.PATH
    $envs.PKG_CONFIG_PATH = "$gst/lib/pkgconfig"
    $envs.PKG_CONFIG_LIBDIR = "$gst/lib/pkgconfig"
    $dir = Join-Path $Private 'build-harness'
    $evidence = Join-Path $Private 'build-evidence'
    $r = Invoke-BoundedProcess $tool.CMake @('-S', $PSScriptRoot, '-B', $dir, '-G', 'NMake Makefiles', '-DCMAKE_BUILD_TYPE=Release', "-DPKG_CONFIG_EXECUTABLE=$gst/bin/pkg-config.exe") $PSScriptRoot "$evidence/adapter-configure.log" 60 $envs
    if ($r.ExitCode) { throw "Adapter configure failed: $($r.Log)" }
    $r = Invoke-BoundedProcess $tool.CMake @('--build', $dir,'--parallel','2') $PSScriptRoot "$evidence/adapter-build.log" 120 $envs
    if ($r.ExitCode) { throw "Adapter build failed: $($r.Log)" }
    & "$PSScriptRoot/package.ps1"
    if ($LASTEXITCODE) { throw 'Package failed' }
    exit 0
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
