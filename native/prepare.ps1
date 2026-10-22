# Requires PowerShell 7; never invokes an installer or changes machine/user PATH.
param([switch]$Offline,[switch]$SourcesOnly)
. "$PSScriptRoot/common.ps1"
try {
    $downloads = Join-Path $Private 'downloads'
    $source = Join-Path $Private 'source'
    $evidence = Join-Path $Private 'build-evidence'
    foreach ($dir in @($downloads, $source, $evidence)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $lock = Get-Content "$PSScriptRoot/dependencies.json" -Raw | ConvertFrom-Json
    foreach ($artifact in $lock.artifacts) {
        $file = Join-Path $downloads $artifact.file
        if (!(Test-Path -LiteralPath $file)) {
            if ($Offline) { throw "Missing cached dependency: $($artifact.file)" }
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest $artifact.url -OutFile "$file.partial"
            if ((Get-FileHash "$file.partial" -Algorithm SHA256).Hash -ne $artifact.sha256) { throw "Hash mismatch: $($artifact.file)" }
            Move-Item -LiteralPath "$file.partial" -Destination $file
        }
        if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $artifact.sha256) { throw "Hash mismatch: $($artifact.file)" }
        if ($artifact.file -like '*.zip') { Expand-Archive -LiteralPath $file -DestinationPath $source -Force }
    }
    $gst = Join-Path $Private 'gstreamer/{app}'
    if (!(Test-Path "$Private/gstreamer/extracted.ok")) {
        $r = Invoke-BoundedProcess "$source/innounp.exe" @('-x', '-y', "-d$Private/gstreamer", "$downloads/gstreamer.exe") $PSScriptRoot "$evidence/extract.log" 600
        if ($r.ExitCode) { throw "Extraction failed: $($r.Log)" }
        Set-Content "$Private/gstreamer/extracted.ok" $lock.artifacts[0].sha256
    }
    # ZIP exports omit submodules. Populate only pinned source dependencies, and
    # replace upstream automatic Git fetches with path checks (no ambient Git/network).
    Copy-Item "$source/ThreadPool-3abd50f329c2bc20e379b3d467774ffd8e3b1343/*" "$source/log4cplus-REL_2_2_1/threadpool" -Recurse -Force
    # Flatten the pinned sources: upstream nested dependency names exceed the
    # compiler's practical Windows include-path limit in this repository path.
    $cpp = Join-Path $source 'cpp'
    $c = Join-Path $source 'c'
    $pic = Join-Path $source 'pic'
    # Never merge a new upstream base with files introduced by an earlier patch.
    # Preserve previous task-owned source trees for diagnosis instead of deleting.
    foreach($target in @($cpp,$c,$pic)) {
        $resolved=[IO.Path]::GetFullPath($target)
        $prefix=[IO.Path]::GetFullPath($source)+[IO.Path]::DirectorySeparatorChar
        if(!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe prepared source path'}
        if(Test-Path -LiteralPath $target){
            if((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Prepared source is a reparse point'}
            Move-Item -LiteralPath $target -Destination ($target+'-previous-'+[guid]::NewGuid().ToString('N'))
        }
    }
    New-Item -ItemType Directory $cpp -Force | Out-Null
    Copy-Item "$source/amazon-kinesis-video-streams-producer-sdk-cpp-3.6.0/*" $cpp -Recurse -Force
    New-Item -ItemType Directory $c -Force | Out-Null
    Copy-Item "$source/amazon-kinesis-video-streams-producer-c-1.6.2/*" $c -Recurse -Force
    New-Item -ItemType Directory $pic -Force | Out-Null
    Copy-Item "$source/amazon-kinesis-video-streams-pic-1.3.1/*" $pic -Recurse -Force
    foreach ($dir in @($cpp, $c)) {
        $file = Join-Path $dir 'CMake/Utilities.cmake'
        $text = Get-Content $file -Raw
        $pattern = '(?s)function\(fetch_repo lib_name\).*?endfunction\(\)'
        if ($text -notmatch $pattern) { throw 'Pinned upstream fetch_repo changed' }
        $replacement = @'
function(fetch_repo lib_name)
  if (lib_name STREQUAL "kvscproducer")
    set(src "${CMAKE_CURRENT_SOURCE_DIR}/../c")
  elseif(lib_name STREQUAL "kvspic")
    set(src "${CMAKE_CURRENT_SOURCE_DIR}/../pic")
  else()
    message(FATAL_ERROR "Unpinned dependency ${lib_name}")
  endif()
  if(NOT EXISTS "${src}/CMakeLists.txt")
    message(FATAL_ERROR "Missing offline dependency ${src}")
  endif()
endfunction()
'@
        [IO.File]::WriteAllText($file, [regex]::Replace($text, $pattern, [Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }))
    }
    $cFile = Join-Path $c 'CMakeLists.txt'
    $text = (Get-Content $cFile -Raw).Replace('add_subdirectory("${DEPENDENCY_DOWNLOAD_PATH}/libkvspic/kvspic-src")', 'add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../pic" "${CMAKE_CURRENT_BINARY_DIR}/pic")')
    [IO.File]::WriteAllText($cFile, $text)
    $file = Join-Path $cpp 'CMakeLists.txt'
    $text = Get-Content $file -Raw
    $pattern = '(?s)if \(WIN32\)\s+if\(EXISTS [^\r\n]*pkg-config.exe"\).*?endif\(\)\s+endif\(\)'
    if ($text -notmatch $pattern) { throw 'Pinned upstream pkg-config block changed' }
    $text = [regex]::Replace($text, $pattern, '# Private pkg-config supplied by the proof runner.')
    # PATHS forces config mode, while the official GStreamer SDK supplies curl's
    # headers/import library. Use CMake's standard FindCURL module as Producer C does.
    $text = $text.Replace('find_package(CURL REQUIRED PATHS ${OPEN_SRC_INSTALL_PREFIX})', 'find_package(CURL REQUIRED MODULE)')
    $text = $text.Replace('add_subdirectory(${DEPENDENCY_DOWNLOAD_PATH}/libkvscproducer/kvscproducer-src EXCLUDE_FROM_ALL)', 'add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../c" "${CMAKE_CURRENT_BINARY_DIR}/c" EXCLUDE_FROM_ALL)')
    [IO.File]::WriteAllText($file, $text)
    if($SourcesOnly){Write-Output 'Pinned source and build prerequisites prepared; no build performed';exit 0}
    & "$PSScriptRoot/apply-credentials-patch.ps1" -SourceRoot $cpp
    if ($LASTEXITCODE) { throw 'Pinned credential patch failed' }
    $tool = Get-NativeToolchain
    $envs = $tool.Environment
    $envs.PATH = "$gst/bin;" + $envs.PATH
    $envs.PKG_CONFIG_PATH = "$gst/lib/pkgconfig"
    $envs.PKG_CONFIG_LIBDIR = "$gst/lib/pkgconfig"
    $deps = Join-Path $Private 'deps'
    $builds = @(
        @{ Name='log4cplus'; Source="$source/log4cplus-REL_2_2_1"; Args=@("-DCMAKE_INSTALL_PREFIX=$deps", '-DLOG4CPLUS_BUILD_TESTING=OFF', '-DWITH_UNIT_TESTS=OFF', '-DLOG4CPLUS_BUILD_LOGGINGSERVER=OFF', '-DLOG4CPLUS_ENABLE_THREAD_POOL=OFF', '-DUNICODE=OFF', '-DBUILD_SHARED_LIBS=ON'); Target='install' },
        @{ Name='producer-flat'; Source=$cpp; Args=@('-DBUILD_DEPENDENCIES=OFF', '-DBUILD_GSTREAMER_PLUGIN=ON', '-DBUILD_TEST=OFF', "-DCMAKE_PREFIX_PATH=$gst;$deps", "-DOPEN_SRC_INSTALL_PREFIX=$deps", "-DPKG_CONFIG_EXECUTABLE=$gst/bin/pkg-config.exe", "-DCURL_LIBRARY=$gst/lib/curl.lib", "-DOPENSSL_SSL_LIBRARY=$gst/lib/ssl.lib", "-DOPENSSL_CRYPTO_LIBRARY=$gst/lib/crypto.lib"); Target='gstkvssink' },
        @{ Name='harness'; Source=$PSScriptRoot; Args=@("-DPKG_CONFIG_EXECUTABLE=$gst/bin/pkg-config.exe","-DSBREC_SDK_C_SOURCE=$c"); Target='all' }
    )
    foreach ($build in $builds) {
        $dir = Join-Path $Private "build-$($build.Name)"
        $args = @('-S', $build.Source, '-B', $dir, '-G', 'NMake Makefiles', '-DCMAKE_BUILD_TYPE=Release', "-DSBREC_SOURCE_DIR=$($PSScriptRoot.Replace('\','/'))") + $build.Args
        $r = Invoke-BoundedProcess $tool.CMake $args $PSScriptRoot "$evidence/$($build.Name)-configure.log" 300 $envs
        if ($r.ExitCode) { throw "Configure failed: $($r.Log)" }
        $r = Invoke-BoundedProcess $tool.CMake @('--build', $dir, '--parallel','2','--target', $build.Target) $PSScriptRoot "$evidence/$($build.Name)-build.log" 900 $envs
        if ($r.ExitCode) { throw "Build failed: $($r.Log)" }
    }
    & "$PSScriptRoot/package.ps1"
    if ($LASTEXITCODE) { throw 'Package failed' }
    Write-Output "Prepared private native proof: $Private/runtime"
    exit 0
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
