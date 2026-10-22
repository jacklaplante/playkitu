# Offline actual-runner/SDK tests. Never point these fake fixtures at production
# kvssink: the test executable refuses a plugin without interception exports.
param([switch]$LifecycleFocus)
. "$PSScriptRoot/common.ps1"
$evidence = Join-Path $Private "synthetic-tests/$([guid]::NewGuid())"
New-Item -ItemType Directory $evidence -Force | Out-Null
try {
    $runtime = Join-Path $Private 'runtime'
    $plugins = Join-Path $evidence 'plugins'
    New-Item -ItemType Directory $plugins | Out-Null
    Get-ChildItem "$runtime/plugins" -File | Where-Object Name -ne 'gstkvssink.dll' | Copy-Item -Destination $plugins
    Copy-Item "$Private/build-producer-flat/offline/gstkvssink.dll" "$plugins/gstkvssink.dll"
    $results = @(); $decodes = 0
    if ((Get-FileHash "$Private/build-harness/PlaytestRecordingNativeProof.dll").Hash -ne (Get-FileHash "$runtime/bin/PlaytestRecordingNativeProof.dll").Hash) { throw 'Runner test/build/package DLL mismatch' }
    $cases = if ($LifecycleFocus) { @('success','stop','cancel','sdk-timeout') }
        else { @('success','replace','received','stop','cancel','overflow','byte-limit','expired','init-fail','timeout','sdk-timeout') }
    foreach ($case in $cases) {
        $dir = Join-Path $evidence $case
        New-Item -ItemType Directory $dir | Out-Null
        $envs = Get-PrivateRuntimeEnvironment $runtime $dir -Region 'eu-west-1'
        $envs.GST_PLUGIN_SYSTEM_PATH=$plugins; $envs.GST_PLUGIN_SYSTEM_PATH_1_0=$plugins
        $r = Invoke-BoundedProcess "$Private/build-harness/native-synthetic-tests.exe" @("$plugins/gstkvssink.dll", "$dir/media.mkv", "$runtime/certs/cert.pem", $case) $dir "$dir/test.log" 35 $envs -MinimalEnvironment
        $text = Get-Content $r.Log -Raw
        foreach ($marker in @('OFFLINEONLYACCESS','OFFLINEONLYSECRET','OFFLINEONLYSESSION')) { if($text.Contains($marker)) { throw "Fake secret leaked: $case" } }
        if ($r.ExitCode -or $text -notmatch 'PASS checks=(\d+)') { throw "Runner case failed: $case ($($r.Log))" }
        $results += @{Case=$case;Checks=[int]$Matches[1];ExitCode=$r.ExitCode;Seconds=$r.Seconds;Log=$r.Log}
        if ($case -in @('success','replace','received','overflow')) {
            $r = Invoke-BoundedProcess "$runtime/bin/native-proof.exe" @('decode',"$dir/media.mkv") $dir "$dir/decode.log" 30 $envs -MinimalEnvironment
            if ($r.ExitCode) { throw "SDK MKV decode failed: $case ($($r.Log))" }
            $decodes++
        }
    }
    $inventory = Get-Content "$runtime/inventory.json" -Raw | ConvertFrom-Json
    foreach ($entry in $inventory) {
        if($entry.Machine -ne 'x64' -or (Get-FileHash (Join-Path $runtime $entry.File)).Hash -ne $entry.SHA256) { throw 'Published package inventory mismatch' }
    }
    @{Passed=$true;Cases=$results;Decodes=$decodes;LifecycleFocus=[bool]$LifecycleFocus;PackagePEs=$inventory.Count;
      OfflinePluginSHA256=(Get-FileHash "$plugins/gstkvssink.dll").Hash;
      ProductionPluginSHA256=(Get-FileHash "$runtime/plugins/gstkvssink.dll").Hash;
      NativeDLLSHA256=(Get-FileHash "$runtime/bin/PlaytestRecordingNativeProof.dll").Hash;
      Limitations='Simulated ACKs prove parsing only; no AWS durability/network/TLS/real-expiry/reconnect or host kill guarantee.'} |
      ConvertTo-Json -Depth 5 | Set-Content "$evidence/result.json"
    Write-Output "Passed $($results.Count) intercepted-SDK runner cases, $decodes SDK MKV decodes, $($inventory.Count) x64 package hashes. Evidence: $evidence"
    exit 0
} catch { Write-Error "$_ Evidence: $evidence" -ErrorAction Continue; exit 1 }
