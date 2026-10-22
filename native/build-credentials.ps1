# Reuse only prepared private native sources/libraries: no downloads, credentials
# discovery, Unity, cloud operations, or global tool changes.
. "$PSScriptRoot/common.ps1"
try {
    & "$PSScriptRoot/apply-credentials-patch.ps1"
    if ($LASTEXITCODE) { throw 'Credential patch verification failed' }
    $tool = Get-NativeToolchain
    $gst = Join-Path $Private 'gstreamer/{app}'
    $envs = $tool.Environment
    $envs.PATH = "$gst/bin;" + $envs.PATH
    $envs.PKG_CONFIG_PATH = "$gst/lib/pkgconfig"
    $envs.PKG_CONFIG_LIBDIR = "$gst/lib/pkgconfig"
    $evidence = Join-Path $Private 'build-evidence'
    $dir = Join-Path $Private 'build-producer-flat'
    $args = @('-S', "$Private/source/cpp", '-B', $dir, '-G', 'NMake Makefiles', '-DCMAKE_BUILD_TYPE=Release',
        "-DSBREC_SOURCE_DIR=$($PSScriptRoot.Replace('\','/'))", '-DBUILD_DEPENDENCIES=OFF', '-DBUILD_GSTREAMER_PLUGIN=ON', '-DBUILD_TEST=OFF',
        "-DCMAKE_PREFIX_PATH=$gst;$Private/deps", "-DOPEN_SRC_INSTALL_PREFIX=$Private/deps", "-DPKG_CONFIG_EXECUTABLE=$gst/bin/pkg-config.exe",
        "-DCURL_LIBRARY=$gst/lib/curl.lib", "-DOPENSSL_SSL_LIBRARY=$gst/lib/ssl.lib", "-DOPENSSL_CRYPTO_LIBRARY=$gst/lib/crypto.lib")
    $r = Invoke-BoundedProcess $tool.CMake $args $PSScriptRoot "$evidence/credentials-configure.log" 120 $envs
    if ($r.ExitCode) { throw "Configure failed: $($r.Log)" }
    foreach ($target in @('gstkvssink', 'native-credentials-tests', 'gstkvssink-offline')) {
        $r = Invoke-BoundedProcess $tool.CMake @('--build', $dir, '--target', $target) $PSScriptRoot "$evidence/$target-build.log" 600 $envs
        if ($r.ExitCode) { throw "Build failed: $($r.Log)" }
    }
    & "$PSScriptRoot/package.ps1"
    if ($LASTEXITCODE) { throw 'Package failed' }
    exit 0
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
