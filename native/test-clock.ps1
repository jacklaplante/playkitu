# Additive clock ABI + actual intercepted SDK + maintained decoder proof only.
. "$PSScriptRoot/common.ps1"
$evidence=Join-Path $Private "clock-tests/$([guid]::NewGuid())"
New-Item -ItemType Directory "$evidence/plugins" -Force|Out-Null
try {
    $runtime=Join-Path $Private 'runtime'
    Get-ChildItem "$runtime/plugins" -File|Where-Object Name -ne 'gstkvssink.dll'|Copy-Item -Destination "$evidence/plugins"
    Copy-Item "$Private/build-producer-flat/offline/gstkvssink.dll" "$evidence/plugins/gstkvssink.dll"
    $results=@()
    $unitEnv=Get-PrivateRuntimeEnvironment $runtime $evidence -Region 'eu-west-1';$unitEnv.GST_DEBUG='0'
    $r=Invoke-BoundedProcess "$Private/build-harness/native-clock-tests.exe" @('unit') $evidence "$evidence/unit.log" 15 $unitEnv -MinimalEnvironment
    if($r.ExitCode -or (Get-Content $r.Log -Raw) -notmatch 'PASS clock_unit_checks=(\d+)'){throw 'Clock unit regression failed'}
    $unitChecks=[int]$Matches[1]
    foreach($mode in @('normal','delay','overflow')) {
        $dir=Join-Path $evidence $mode;New-Item -ItemType Directory $dir|Out-Null
        $envs=Get-PrivateRuntimeEnvironment $runtime $dir -Region 'eu-west-1';$envs.GST_DEBUG='0'
        $envs.GST_PLUGIN_SYSTEM_PATH=$envs.GST_PLUGIN_SYSTEM_PATH_1_0="$evidence/plugins"
        $exe=if($mode -eq 'delay'){'native-clock-delay-tests.exe'}else{'native-clock-tests.exe'}
        $runId=[guid]::NewGuid().ToString()
        $r=Invoke-BoundedProcess "$Private/build-harness/$exe" @("$evidence/plugins/gstkvssink.dll","$dir/media.mkv","$runtime/certs/cert.pem","$dir/native.json",$mode,$runId) $dir "$dir/native.log" 35 $envs -MinimalEnvironment
        $log=Get-Content $r.Log -Raw
        if($r.ExitCode -or $log -notmatch 'PASS clock_integration_checks='){throw "Clock integration failed: $mode ($($r.Log))"}
        if($log -match 'OFFLINEONLY'){throw 'Fixture marker leaked'}
        $native=Get-Content "$dir/native.json" -Raw|ConvertFrom-Json
        if($native.runId -ne $runId){throw 'Native run provenance mismatch'}
        $valid=@($native.anchors|Where-Object {$_.kind -eq 1 -and $_.reason -eq 0 -and ($_.flags -band 15) -eq 15})
        if($mode -eq 'overflow') {
            if($valid.Count){throw 'Overflow created a valid mapping'}
            $results+=@{Mode=$mode;NativeMappings=0;Unavailable=$true};continue
        }
        $targets=@($valid|ForEach-Object {([bigint]$_.sdkPts100ns*100).ToString()})
        $r=Invoke-BoundedProcess "$runtime/bin/native-media-probe.exe" @("$dir/media.mkv","$dir/decoded.json",'2048','30000','2097152','--select-video-pts-ns',($targets -join ','),'--tolerance-ns','1000000') $dir "$dir/decode.log" 35 $envs -MinimalEnvironment
        if($r.ExitCode){throw "Clock selected decode failed: $mode"}
        $probe=Get-Content "$dir/decoded.json" -Raw|ConvertFrom-Json
        if($probe.producerAnchor.available -or $probe.continuousCoverageClaim){throw 'Probe promoted a generic anchor/coverage claim'}
        $mapped=@();$id=0
        foreach($anchor in $valid) {
            $q=$probe.frameSelections.queries[$id++]
            if($q.status -ne 'unique_observed' -or $q.matchCount -ne '1'){throw "Selected SDK frame not uniquely decoded: $mode"}
            $support=@($native.anchors|Where-Object {$_.kind -eq 3 -and $_.clockEpoch -eq $anchor.clockEpoch -and $_.frameOrdinal -eq $anchor.frameOrdinal})
            if($support.Count -ne 2){throw 'Missing clock support pair'}
            $point=[bigint]$anchor.pipelineBaseTimeNs+[bigint]$anchor.sourceRunningNs
            $lower=$support|Sort-Object {[bigint]$_.gstClockTimeNs}|Select-Object -First 1
            $upper=$support|Sort-Object {[bigint]$_.gstClockTimeNs}|Select-Object -Last 1
            if([bigint]$lower.gstClockTimeNs -ge $point -or [bigint]$upper.gstClockTimeNs -le $point){throw 'Scheduling target not strictly enclosed'}
            $mid=[bigint]$anchor.estimatedScheduleQpc
            if($mid -lt [bigint]$lower.qpcBefore -or $mid -gt [bigint]$upper.qpcAfter){throw 'Estimated QPC not enclosed'}
            $elapsed=(($mid-[bigint]$native.sessionAnchorQpc)*1000000000)/[bigint]$native.qpcFrequency
            $mapped+=@{FrameOrdinal=$anchor.frameOrdinal;ClockEpoch=$anchor.clockEpoch;SdkPts100ns=$anchor.sdkPts100ns;
              SessionElapsedNs=$elapsed.ToString();ScheduleQpc=$anchor.estimatedScheduleQpc;UncertaintyNs=$anchor.scheduleUncertaintyNs;
              DecodedPtsNs=$q.ptsNs;PlaybackStreamNs=$q.streamNs;DecodedBufferOrdinal=$q.bufferOrdinal;
              QuantizationToleranceNs='1000000';Evidence='unique selected video point in this offline run/file; not continuous coverage'}
        }
        if($mapped.Count -lt 2){throw 'Later-frame correspondence missing'}
        # Missing opening/tail queries remain unavailable rather than nearest-frame matches.
        $first=[bigint]($probe.tracks|Where-Object kind -eq 'video').decoded.firstPtsNs
        $last=[bigint]($probe.tracks|Where-Object kind -eq 'video').decoded.lastPtsNs
        $missing="$(($first-1000000000).ToString()),$(($last+1000000000).ToString())"
        $r=Invoke-BoundedProcess "$runtime/bin/native-media-probe.exe" @("$dir/media.mkv","$dir/missing.json",'2048','30000','2097152','--select-video-pts-ns',$missing,'--tolerance-ns','1000000') $dir "$dir/missing.log" 35 $envs -MinimalEnvironment
        if($r.ExitCode){throw 'Missing-frame probe failed'}
        $absence=Get-Content "$dir/missing.json" -Raw|ConvertFrom-Json
        if(@($absence.frameSelections.queries|Where-Object status -ne 'missing').Count){throw 'Missing frames were guessed'}
        foreach($badQuery in @('1,1','9223372036854775808')) {
            $rejectPath=Join-Path $dir 'rejected-selection.json'
            $r=Invoke-BoundedProcess "$runtime/bin/native-media-probe.exe" @("$dir/media.mkv",$rejectPath,'2048','30000','2097152','--select-video-pts-ns',$badQuery,'--tolerance-ns','1000000') $dir "$dir/rejected-selection.log" 5 $envs -MinimalEnvironment
            if($r.ExitCode -ne 2 -or (Test-Path -LiteralPath $rejectPath)){throw 'Invalid selection was accepted or reserved output'}
        }
        @{Schema=2;SessionId=$native.sessionId;RunId=$native.runId;FileSHA256=(Get-FileHash "$dir/media.mkv").Hash;
          RunnerVariant=$(if($mode -eq 'delay'){'source-included actual runner with test-only 250ms delay'}else{'packaged DLL'});
          CallerClock=$native.callerClock;ManagedStopwatchProof=$false;Coverage='selected observed points only';Mappings=$mapped} |
          ConvertTo-Json -Depth 6|Set-Content "$dir/correspondence.json"
        $results+=@{Mode=$mode;NativeMappings=$valid.Count;DecodedMappings=$mapped.Count;MissingQueries=2}
    }
    $inventory=Get-Content "$runtime/inventory.json" -Raw|ConvertFrom-Json
    foreach($entry in $inventory){if($entry.Machine -ne 'x64' -or (Get-FileHash (Join-Path $runtime $entry.File)).Hash -ne $entry.SHA256){throw 'Clock package inventory mismatch'}}
    @{Passed=$true;UnitChecks=$unitChecks;Cases=$results;PackagePEs=$inventory.Count;ABI1=1;ABI1RecordBytes=264;ABI2=2;ABI2RecordBytes=256;
      NativeDLLSHA256=(Get-FileHash "$runtime/bin/PlaytestRecordingNativeProof.dll").Hash;
      ProbeSHA256=(Get-FileHash "$runtime/bin/native-media-probe.exe").Hash;ManagedOrLiveExecution=$false}|
      ConvertTo-Json -Depth 5|Set-Content "$evidence/result.json"
    Write-Output "Passed measured clock/selected-frame proof and bounds. Evidence: $evidence"
    exit 0
} catch { Write-Error "$_ Evidence: $evidence" -ErrorAction Continue;exit 1 }
