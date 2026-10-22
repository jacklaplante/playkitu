param([string]$RuntimeName = "runtime", [string]$RunnerDll, [string]$ProducerDll)
. "$PSScriptRoot/common.ps1"
try {
    if ($RuntimeName -notmatch "^runtime(?:-C2-[a-zA-Z0-9-]+)?$") { throw "Invalid task runtime name" }
    if (!$RunnerDll) { $RunnerDll = Join-Path $Private "build-harness/PlaytestRecordingNativeProof.dll" }
    if (!$ProducerDll) { $ProducerDll = Join-Path $Private "build-producer-flat/gstkvssink.dll" }
    $gst = Join-Path $Private 'gstreamer/{app}'
    $evidence = Join-Path $Private 'build-evidence'
    # Build a fresh package without merging stale plugins, and preserve the old
    # package if extraction/build/closure fails. Loaded Windows DLLs may prevent
    # replacement; that failure must not partially delete the working package.
    $runtime = Join-Path $Private "runtime-staging-$([guid]::NewGuid())"
    $published = Join-Path $Private $RuntimeName
    foreach ($dir in @($runtime, "$runtime/bin", "$runtime/plugins", "$runtime/licenses")) { New-Item -ItemType Directory $dir -Force | Out-Null }
    Set-Content "$runtime/.proof-owned" 'B0a native proof only'

    # Extract the Microsoft-signed VC redist already inside the pinned GStreamer
    # archive. No installer is executed. PE/CAB unpacking uses standard 7-Zip;
    # only x64 minimum/additional CRT payloads are selected from the Burn manifest.
    $seven = Join-Path $Private '7zip'
    $r = Invoke-BoundedProcess "$Private/downloads/7zr.exe" @('x', "$Private/downloads/7zip.exe", "-o$seven", '-y') $PSScriptRoot "$evidence/7zip-extract.log"
    if ($r.ExitCode) { throw '7-Zip extraction failed' }
    $vc = Join-Path $Private 'vcredist'
    foreach ($mode in @('PE', 'Cab')) {
        $r = Invoke-BoundedProcess "$seven/7z.exe" @('x', "-t$mode", "$Private/gstreamer/{tmp}/vc_redist.x64.exe", "-o$vc/$mode", '-y') $PSScriptRoot "$evidence/vc-$mode.log"
        if ($r.ExitCode) { throw 'VC redist extraction failed' }
    }
    $bytes = [IO.File]::ReadAllBytes("$vc/PE/[0]")
    $firstSize = [BitConverter]::ToUInt32($bytes, 8)
    $offset = [Text.Encoding]::ASCII.GetString($bytes).IndexOf('MSCF', $firstSize)
    if ($offset -lt 0) { throw 'Pinned VC payload CAB missing' }
    $length = [BitConverter]::ToUInt32($bytes, $offset + 8)
    if ($length -gt $bytes.Length - $offset) { throw 'Invalid VC payload CAB' }
    $cab = [byte[]]::new($length)
    [Array]::Copy($bytes, $offset, $cab, 0, $length)
    [IO.File]::WriteAllBytes("$vc/payload.cab", $cab)
    $r = Invoke-BoundedProcess "$seven/7z.exe" @('x', "$vc/payload.cab", "-o$vc/payload", '-y') $PSScriptRoot "$evidence/vc-payload.log"
    if ($r.ExitCode) { throw 'VC payload extraction failed' }
    [xml]$manifest = Get-Content -LiteralPath "$vc/Cab/0" -Raw
    $payloads = @($manifest.BurnManifest.Payload | Where-Object { $_.FilePath -match '^packages\\vcRuntime(Minimum|Additional)_amd64\\cab1.cab$' })
    if ($payloads.Count -ne 2) { throw 'Pinned VC manifest changed' }
    foreach ($payload in $payloads) {
        $r = Invoke-BoundedProcess "$seven/7z.exe" @('x', "$vc/payload/$($payload.SourcePath)", "-o$vc/dlls", '-y') $PSScriptRoot "$evidence/vc-$($payload.SourcePath).log"
        if ($r.ExitCode) { throw 'VC DLL extraction failed' }
    }
    Copy-Item -LiteralPath "$vc/Cab/u4" -Destination "$runtime/licenses/Microsoft-VC-license.rtf"
    $tool = Get-NativeToolchain
    $dumpbin = Join-Path $tool.Environment.VCToolsInstallDir 'bin/Hostx64/x64/dumpbin.exe'
    & "$PSScriptRoot/apply-credentials-patch.ps1"
    if ($LASTEXITCODE) { throw 'Refusing package with unverified credential patch' }
    $r = Invoke-BoundedProcess $dumpbin @('/exports', $ProducerDll) $PSScriptRoot "$evidence/credential-exports.log" 30 $tool.Environment
    if ($r.ExitCode) { throw 'Credential export inspection failed' }
    $exports = Get-Content $r.Log -Raw
    foreach ($symbol in @('create', 'update', 'release', 'bind')) {
        if ($exports -notmatch "\bsbrec_credentials_$symbol\b") { throw 'Refusing package with missing memory-credential export' }
    }
    if ($exports -notmatch '\bsbrec_synthetic_observer_abi_version\b' -or $exports -match '\bsbrec_test_configure\b') { throw 'Refusing package with stale observer ABI or test transport' }
    if ($exports -notmatch '\bsbrec_sink_retire\b') { throw 'Refusing package without explicit SDK retirement' }
    $r = Invoke-BoundedProcess $dumpbin @('/exports', $RunnerDll) $PSScriptRoot "$evidence/runner-exports.log" 30 $tool.Environment
    if ($r.ExitCode) { throw 'Runner export inspection failed' }
    $runnerExports = Get-Content $r.Log -Raw
    foreach ($symbol in @('abi_version','trusted_region_version','clock','create','start','update_credentials','status','drain_observations','request_stop','release')) {
        if ($runnerExports -notmatch "\bsbrec_runner_$symbol\b") { throw 'Refusing package with missing runner export' }
    }
    foreach($symbol in @('clock_v2_info','enable_clock_v2','drain_clock_v2')) {
        if($runnerExports -notmatch "\bsbrec_runner_$symbol\b"){throw 'Missing additive clock ABI2 export'}
    }
    foreach($symbol in @('external_v1_info','enable_external_v1','push_external_v1','external_status_v1','drain_external_v1')) {
        if($runnerExports -notmatch "\bsbrec_runner_$symbol\b"){throw 'Missing external media input V1 export'}
    }
    @{ Schema=1; Producer='3.6.0'; PatchSHA256=(Get-FileHash "$PSScriptRoot/patches/kvssink-memory-provider.patch").Hash;
       SourceSHA256=(Get-FileHash "$PSScriptRoot/native-session-credentials.cpp").Hash;
       HeaderSHA256=(Get-FileHash "$PSScriptRoot/native-session-credentials.h").Hash;
       LoggingPatchSHA256=(Get-FileHash "$PSScriptRoot/patches/producer-c-private-logging.patch").Hash;
       LoggingTestsSHA256=(Get-FileHash "$PSScriptRoot/tests/sdk-logging.cpp").Hash;
       OfflineTransportSHA256=(Get-FileHash "$PSScriptRoot/tests/sdk-response-offline.c").Hash } |
        ConvertTo-Json | Set-Content "$runtime/credential-provider.json"
    $candidates = @{}
    foreach ($dir in @("$gst/bin", "$Private/deps/bin", "$vc/dlls")) {
        foreach ($file in Get-ChildItem -LiteralPath $dir -File) {
            if ($file.Name -match '\.dll(_amd64)?$') { $candidates[($file.Name -replace '_amd64$', '').ToLowerInvariant()] = $file.FullName }
        }
    }
    $queue = [Collections.Generic.Queue[string]]::new()
    foreach ($name in @('coreelements', 'app', 'audioconvert', 'videotestsrc', 'audiotestsrc', 'videoconvertscale', 'mediafoundation', 'videoparsersbad', 'audioparsers', 'matroska', 'd3d11', 'pango')) {
        $dest = "$runtime/plugins/gst$name.dll"
        Copy-Item -LiteralPath "$gst/lib/gstreamer-1.0/gst$name.dll" -Destination $dest
        $queue.Enqueue($dest)
    }
    $dest = "$runtime/plugins/gstkvssink.dll"
    Copy-Item -LiteralPath $ProducerDll -Destination $dest
    $queue.Enqueue($dest)
    foreach ($pair in @(
        @("$Private/build-harness/native-proof.exe", 'native-proof.exe'),
        @("$Private/build-harness/native-media-probe.exe", 'native-media-probe.exe'),
        @($RunnerDll, 'PlaytestRecordingNativeProof.dll'),
        @("$gst/bin/gst-inspect-1.0.exe", 'gst-inspect-1.0.exe'),
        @("$gst/bin/gst-launch-1.0.exe", 'gst-launch-1.0.exe'),
        @("$gst/libexec/gstreamer-1.0/gst-plugin-scanner.exe", 'gst-plugin-scanner.exe'))) {
        $dest = "$runtime/bin/$($pair[1])"
        Copy-Item -LiteralPath $pair[0] -Destination $dest
        $queue.Enqueue($dest)
    }
    $inventory = [Collections.Generic.List[object]]::new()
    New-Item -ItemType Directory "$runtime/certs" | Out-Null
    Copy-Item -LiteralPath "$Private/source/cpp/certs/cert.pem" -Destination "$runtime/certs/cert.pem"
    @{ Schema=1; RunnerABI=1; ClockABI=2; ClockRecordBytes=256; ExternalInputABI=1; ExternalInputBytes=72; ExternalStatusBytes=88; ExternalObservationBytes=72;
       RunnerDllSHA256=(Get-FileHash $RunnerDll).Hash;
       ClockSourceSHA256=(Get-FileHash "$PSScriptRoot/native-media-clock.cpp").Hash;
       ClockHeaderSHA256=(Get-FileHash "$PSScriptRoot/native-media-clock.h").Hash;
       SourceSHA256=(Get-FileHash "$PSScriptRoot/native-synthetic-runner.cpp").Hash;
       HeaderSHA256=(Get-FileHash "$PSScriptRoot/native-synthetic-runner.h").Hash;
       MediaSHA256=(Get-FileHash "$PSScriptRoot/native-synthetic-media.cpp").Hash;
       ObserverSHA256=(Get-FileHash "$PSScriptRoot/native-synthetic-media.h").Hash;
       PublicTrustSHA256=(Get-FileHash "$runtime/certs/cert.pem").Hash;
       Overlay='pinned GStreamer 1.28.8 timeoverlay/textoverlay (Pango); Windows Consolas font';
       TrustedRegionCapability=1; SubmittedUtcQuantizationBound100ns=10000; UtcTranslationGridNs=1000000;
       TestTransportPackaged=$false } | ConvertTo-Json | Set-Content "$runtime/synthetic-runner.json"
    $seen = @{}
    while ($queue.Count) {
        $file = $queue.Dequeue()
        $name = [IO.Path]::GetFileName($file).ToLowerInvariant()
        if ($seen.ContainsKey($name)) { continue }
        $seen[$name] = $true
        $r = Invoke-BoundedProcess $dumpbin @('/headers', '/dependents', $file) $PSScriptRoot "$evidence/pe-$name.log" 30 $tool.Environment
        if ($r.ExitCode) { throw "PE inspection failed: $name" }
        $text = Get-Content $r.Log -Raw
        if ($text -notmatch '8664 machine \(x64\)') { throw "Not an x64 PE: $name" }
        $dependencies = @([regex]::Matches($text, '(?mi)^\s+([a-z0-9_.-]+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() } | Sort-Object -Unique)
        foreach ($dep in $dependencies) {
            if ($candidates.ContainsKey($dep)) {
                $dest = "$runtime/bin/$dep"
                if (!(Test-Path -LiteralPath $dest)) { Copy-Item -LiteralPath $candidates[$dep] -Destination $dest }
                $queue.Enqueue($dest)
            } elseif ($dep -match '^(msvcp|vcruntime|concrt)[0-9]' -or ($dep -notmatch '^(api-ms-|ext-ms-)' -and !(Test-Path -LiteralPath "$env:SystemRoot/System32/$dep"))) {
                throw "Missing private dependency: $dep (from $name)"
            }
        }
        $inventory.Add([pscustomobject]@{ File=$file.Substring($runtime.Length+1); Machine='x64'; SHA256=(Get-FileHash $file).Hash; Dependencies=$dependencies; Version=(Get-Item -LiteralPath $file).VersionInfo.FileVersion })
    }
    Copy-Item "$gst/share/licenses/*" "$runtime/licenses" -Recurse -Force
    Copy-Item -LiteralPath "$gst/share/versions.txt" -Destination "$runtime/versions.txt"
    foreach ($dir in @('amazon-kinesis-video-streams-producer-sdk-cpp-3.6.0', 'amazon-kinesis-video-streams-producer-c-1.6.2', 'amazon-kinesis-video-streams-pic-1.3.1', 'log4cplus-REL_2_2_1', 'ThreadPool-3abd50f329c2bc20e379b3d467774ffd8e3b1343')) {
        $target = "$runtime/licenses/$dir"
        New-Item -ItemType Directory $target -Force | Out-Null
        Get-ChildItem "$Private/source/$dir" -File | Where-Object Name -match '^(LICENSE|NOTICE|COPYING)' | Copy-Item -Destination $target
    }
    $inventory | ConvertTo-Json -Depth 5 | Set-Content "$runtime/inventory.json"
    $root = [IO.Path]::GetFullPath($Private) + [IO.Path]::DirectorySeparatorChar
    $previous = Join-Path $Private "runtime-previous-$([guid]::NewGuid())"
    foreach ($path in @($runtime, $published, $previous)) {
        if (![IO.Path]::GetFullPath($path).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package move' }
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Package path is a reparse point' }
    }
    if (Test-Path -LiteralPath $published) {
        if (!(Test-Path -LiteralPath "$published/.proof-owned")) { throw 'Refusing to replace unowned runtime' }
        [IO.Directory]::Move($published, $previous)
    }
    try { [IO.Directory]::Move($runtime, $published) }
    catch {
        if (Test-Path -LiteralPath $previous) { [IO.Directory]::Move($previous, $published) }
        throw
    }
    Write-Output "Packaged $($inventory.Count) x64 PE files; inventory: $published/inventory.json"
    exit 0
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
