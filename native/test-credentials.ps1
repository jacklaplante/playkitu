. "$PSScriptRoot/common.ps1"
$evidence = Join-Path $Private "credential-tests/$([guid]::NewGuid())"
New-Item -ItemType Directory $evidence -Force | Out-Null
try {
    $runtime = Join-Path $Private 'runtime'
    $envs = Get-PrivateRuntimeEnvironment $runtime $evidence -Region 'eu-west-1'
    $r = Invoke-BoundedProcess "$Private/build-producer-flat/native-credentials-tests.exe" @("$runtime/plugins/gstkvssink.dll") $evidence "$evidence/native-tests.log" 60 $envs -MinimalEnvironment
    if ($r.ExitCode) { throw "Native credential tests failed: $($r.Log)" }
    $text = Get-Content $r.Log -Raw
    if ($text -notmatch 'PASS checks=(\d+) concurrent_reads=8000') { throw 'Native test summary missing' }
    $count = [int]$Matches[1]
    if ($text -notmatch 'sdk_logging_checks=(\d+) intercepted_transport_calls=1') { throw 'Actual offline SDK signing/HTTP-failure regression missing' }
    $loggingChecks = [int]$Matches[1]
    if ($text -notmatch 'redaction_debug_clean=1 redaction_http_clean=1' -or $text -notmatch 'signed_headers_sha256=4098242c287a43328c0ee55d84b06cb5e93e325eb33335f01b6de2a7b709554c') { throw 'SDK logging redaction/signing golden failed' }
    # The fake secret/token and thrown exception are known markers; assert neither
    # native SDK callbacks nor error paths emitted their sensitive contents.
    foreach ($marker in @('FAKE_SECRET', 'FAKE_SESSION_TOKEN', 'FAKEACCESS', 'FAKE_SENSITIVE_EXCEPTION_MARKER', 'FAKE_REPLACEMENT')) {
        if ($text.Contains($marker)) { throw 'Native diagnostics leaked a fake sensitive marker' }
    }
    $shell = (Get-Process -Id $PID).Path
    $inventoryHash = (Get-FileHash "$runtime/inventory.json").Hash
    $r = Invoke-BoundedProcess $shell @('-NoProfile', '-File', "$PSScriptRoot/apply-credentials-patch.ps1") $evidence "$evidence/patch-idempotent.log" 30
    if ($r.ExitCode) { throw 'Idempotent patch verification failed' }
    $source = Join-Path $Private 'source/cpp/CMakeLists.txt'
    $before = [IO.File]::ReadAllBytes($source)
    try {
        [IO.File]::AppendAllText($source, "`n# deliberately unexpected offline-test source`n")
        $r = Invoke-BoundedProcess $shell @('-NoProfile', '-File', "$PSScriptRoot/apply-credentials-patch.ps1") $evidence "$evidence/patch-tampered.log" 30
        if ($r.ExitCode -ne 1) { throw 'Unexpected patch base was accepted' }
    } finally { [IO.File]::WriteAllBytes($source, $before) }
    $source = Join-Path $Private 'source/c/src/source/Response.c'
    $before = [IO.File]::ReadAllBytes($source)
    try {
        [IO.File]::AppendAllText($source, "`n/* deliberately unexpected offline-test logging source */`n")
        $r = Invoke-BoundedProcess $shell @('-NoProfile', '-File', "$PSScriptRoot/apply-credentials-patch.ps1") $evidence "$evidence/patch-logging-tampered.log" 30
        if ($r.ExitCode -ne 1) { throw 'Unexpected SDK logging patch base was accepted' }
    } finally { [IO.File]::WriteAllBytes($source, $before) }
    if ((Get-FileHash "$runtime/inventory.json").Hash -ne $inventoryHash) { throw 'Failed patch changed published package' }
    $inventory = Get-Content "$runtime/inventory.json" -Raw | ConvertFrom-Json
    foreach ($entry in $inventory) {
        if ($entry.Machine -ne 'x64' -or (Get-FileHash -LiteralPath (Join-Path $runtime $entry.File)).Hash -ne $entry.SHA256) { throw 'Package PE hash/machine inventory mismatch' }
    }
    @{ Passed=$true; NativeChecks=$count; SdkLoggingChecks=$loggingChecks; DebugSigningRedacted=$true; HttpFailureRedacted=$true; SignedHeaderGolden='4098242c287a43328c0ee55d84b06cb5e93e325eb33335f01b6de2a7b709554c'; ConcurrentSdkReads=8000; RedactionMarkers=5; PatchChecks=3; PackagePeFiles=$inventory.Count; SourceRestored=$true; NativeLog="$evidence/native-tests.log" } |
        ConvertTo-Json | Set-Content "$evidence/result.json"
    Write-Output "Passed $count native checks, 8000 concurrent reads, redaction/patch/inventory checks. Evidence: $evidence"
    exit 0
} catch { Write-Error "$_ Evidence: $evidence" -ErrorAction Continue; exit 1 }
