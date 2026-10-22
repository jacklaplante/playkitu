param([string]$SdkFixtureDirectory='', [string]$RuntimeDirectory='', [string]$ProbeBuildDirectory='')
. "$PSScriptRoot/common.ps1"
$evidence=Join-Path $Private "probe-tests/$([guid]::NewGuid())"
New-Item -ItemType Directory $evidence | Out-Null
try {
    $runtime=if($RuntimeDirectory){[IO.Path]::GetFullPath($RuntimeDirectory)}else{Join-Path $Private 'runtime'}
    $build=if($ProbeBuildDirectory){[IO.Path]::GetFullPath($ProbeBuildDirectory)}else{Join-Path $Private 'build-harness'}
    $envs=Get-PrivateRuntimeEnvironment $runtime $evidence -Region 'eu-west-1'
    $envs.GST_DEBUG='0'
    $cases=[Collections.Generic.List[object]]::new()
    $r=Invoke-BoundedProcess "$build/native-media-probe-tests.exe" @('unit') $evidence "$evidence/unit.log" 10 $envs -MinimalEnvironment
    if($r.ExitCode -or (Get-Content $r.Log -Raw) -notmatch 'PASS unit_checks=(\d+) later_segment_negative_control=1') { throw 'Probe unit/negative control failed' }
    $unitChecks=[int]$Matches[1]
    foreach($kind in @('normal','gapped')) {
        $r=Invoke-BoundedProcess "$build/native-media-probe-tests.exe" @('fixture',"$evidence/$kind.mkv",$kind) $evidence "$evidence/$kind-generate.log" 30 $envs -MinimalEnvironment
        if($r.ExitCode) { throw "Maintained generated-media fixture failed: $kind" }
    }
    function Invoke-Probe([string]$Name,[string]$InputFile,[int]$Expected,[int]$Spans=4096,[int]$Timeout=30000,[int]$Bytes=2097152) {
        $output=Join-Path $evidence "$Name.json"
        $r=Invoke-BoundedProcess "$runtime/bin/native-media-probe.exe" @($InputFile,$output,"$Spans","$Timeout","$Bytes") $evidence "$evidence/$Name.log" 35 $envs -MinimalEnvironment
        if($r.ExitCode -ne $Expected) { throw "Probe $Name exit $($r.ExitCode), expected $Expected" }
        $result=Get-Content $output -Raw | ConvertFrom-Json
        if($result.schema -ne 1 -or $result.continuousCoverageClaim -or $result.producerAnchor.available -or $result.producerAnchor.playbackOffsetVerified) { throw 'Probe made an unsupported coverage/anchor claim' }
        if($result.fileSha256 -and $result.fileSha256 -ne (Get-FileHash -LiteralPath $InputFile).Hash) { throw 'Probe input SHA mismatch' }
        if((Get-Item $output).Length -gt $Bytes) { throw 'Probe exceeded output bound' }
        if($Expected -ne 0 -and $result.status -ne 'incomplete') { throw 'Failed probe claimed successful decoding' }
        $cases.Add(@{Name=$Name;ExitCode=$r.ExitCode;Status=$result.status;Reason=$result.reason;Output=$output})
        return $result
    }
    $normal=Invoke-Probe 'normal' "$evidence/normal.mkv" 0
    if(!$normal.decodeEos -or $normal.tracks.Count -ne 2 -or $normal.sourceOpeningKnown -or $normal.sourceTailKnown -or $normal.fileCompleteness -ne 'unknown') { throw 'Normal decode/evidence-level check failed' }
    $gap=Invoke-Probe 'gapped' "$evidence/gapped.mkv" 0
    $video=$gap.tracks | Where-Object kind -eq 'video'
    $hole=@($video.decoded.spans | Where-Object reason -eq 'timestamp_hole' | Where-Object { ([bigint]$_.firstPtsNs-[bigint]$_.previousEndPtsNs) -gt 500000000 })
    if(!$hole.Count) { throw 'Monotonic missing GOP was silently bridged' }
    $sdkSource=if($SdkFixtureDirectory) { [IO.Path]::GetFullPath($SdkFixtureDirectory) }
        else { Join-Path $Private 'synthetic-tests/c03f695a-0aaa-451c-a051-96fe17d40571/success' }
    if(!(Test-Path "$sdkSource/media.mkv") -or !(Test-Path "$sdkSource/test.log")) { throw 'Supply -SdkFixtureDirectory with an existing intercepted B2c success fixture (media.mkv and test.log)' }
    Copy-Item "$sdkSource/media.mkv" "$evidence/sdk.mkv"
    $sdk=Invoke-Probe 'sdk' "$evidence/sdk.mkv" 0
    $sdkVideo=$sdk.tracks | Where-Object kind -eq 'video'
    $sdkAudio=$sdk.tracks | Where-Object kind -eq 'audio'
    if([long]$sdkVideo.decoded.buffers -le 0 -or [long]$sdkAudio.decoded.buffers -le 0 -or !$sdkVideo.demux.segments.Count) { throw 'SDK AV demux/decode evidence missing' }
    $log=Get-Content "$sdkSource/test.log" -Raw
    if($log -notmatch 'video_sdk_100ns=(\d+)') { throw 'Recorded SDK anchor context missing' }
    $sdkFirst=$Matches[1]
    if($log -notmatch 'video=(\d+) audio=(\d+) source=(\d+)') { throw 'SDK acceptance context missing' }
    $acceptedVideo=$Matches[1]; $acceptedAudio=$Matches[2]
    $demuxFirst=$sdkVideo.demux.spans[0].firstPtsNs
    @{Source='accepted B2c intercepted SDK fixture; simulated ACKs';
      ProducerRunId='00000000-0000-4000-8000-000000000002';SdkFirstVideoPts100ns=$sdkFirst;
      DemuxFirstVideoPtsNs=$demuxFirst;DecodedFirstVideoPtsNs=$sdkVideo.decoded.spans[0].firstPtsNs;
      DecodedFirstAudioPtsNs=$sdkAudio.decoded.spans[0].firstPtsNs;
      AcceptedVideoBuffers=$acceptedVideo;AcceptedAudioBuffers=$acceptedAudio;
      DecodedVideoBuffers=$sdkVideo.decoded.buffers;DecodedAudioBuffers=$sdkAudio.decoded.buffers;
      FragmentIdsAvailable=$false;MissingOpeningOrTailPositionVerified=$false;
      CandidateProducerMinusDemuxNs=(([bigint]$sdkFirst*100)-[bigint]$demuxFirst).ToString();
      OffsetVerified=$false;Reason='Observed SDK/demux times compared, but first frame and fragment identity are not independently matched. No verified producer-to-playback anchor.'} |
      ConvertTo-Json | Set-Content "$evidence/sdk-context-unverified.json"
    $bytes=[IO.File]::ReadAllBytes("$evidence/normal.mkv")
    [IO.File]::WriteAllBytes("$evidence/truncated.mkv",$bytes[0..([int]($bytes.Length/2)-1)])
    # Truncated Matroska can still report EOS. Neither outcome proves the tail.
    $output="$evidence/truncated.json"
    $r=Invoke-BoundedProcess "$runtime/bin/native-media-probe.exe" @("$evidence/truncated.mkv",$output) $evidence "$evidence/truncated.log" 35 $envs -MinimalEnvironment
    if($r.ExitCode -notin @(0,3)) { throw 'Truncated probe failed unexpectedly' }
    $truncated=Get-Content $output -Raw|ConvertFrom-Json
    if($truncated.sourceTailKnown -or $truncated.continuousCoverageClaim -or $truncated.fileCompleteness -ne 'unknown') { throw 'Truncated input implied complete source coverage' }
    $cases.Add(@{Name='truncated';ExitCode=$r.ExitCode;Status=$truncated.status;Reason=$truncated.reason;Output=$output})
    [IO.File]::WriteAllText("$evidence/corrupt.mkv",'not a media file')
    $null=Invoke-Probe 'corrupt' "$evidence/corrupt.mkv" 3
    [IO.File]::WriteAllBytes("$evidence/empty.mkv",[byte[]]::new(0))
    $null=Invoke-Probe 'empty' "$evidence/empty.mkv" 3
    $limited=Invoke-Probe 'span-limit' "$evidence/normal.mkv" 3 1
    if($limited.reason -ne 'span_limit') { throw 'Span cap not explicit' }
    $limited=Invoke-Probe 'output-limit' "$evidence/normal.mkv" 3 4096 30000 1024
    if($limited.reason -ne 'output_limit') { throw 'Output cap not explicit' }
    $limited=Invoke-Probe 'deadline' "$evidence/normal.mkv" 3 4096 100
    if($limited.reason -ne 'deadline') { throw 'Probe deadline not explicit' }
    $large=[IO.File]::Create("$evidence/oversized.mkv");$large.SetLength(64MB+1);$large.Dispose()
    $limited=Invoke-Probe 'input-limit' "$evidence/oversized.mkv" 3
    if($limited.reason -ne 'input_limit') { throw 'Input cap not explicit' }
    $before=(Get-FileHash "$evidence/normal.json").Hash
    $r=Invoke-BoundedProcess "$runtime/bin/native-media-probe.exe" @("$evidence/normal.mkv","$evidence/normal.json") $evidence "$evidence/no-overwrite.log" 10 $envs -MinimalEnvironment
    if($r.ExitCode -ne 2 -or (Get-FileHash "$evidence/normal.json").Hash -ne $before) { throw 'Existing probe evidence overwritten' }
    $cases.Add(@{Name='no-overwrite';ExitCode=$r.ExitCode})
    $inventory=Get-Content "$runtime/inventory.json" -Raw|ConvertFrom-Json
    foreach($entry in $inventory) {
        if($entry.Machine -ne 'x64' -or (Get-FileHash (Join-Path $runtime $entry.File)).Hash -ne $entry.SHA256) { throw 'Probe package inventory mismatch' }
    }
    @{Passed=$true;UnitChecks=$unitChecks;Cases=$cases;PackagePEs=$inventory.Count;SdkFixtureSHA256=(Get-FileHash "$evidence/sdk.mkv").Hash;
      ProbeSHA256=(Get-FileHash "$runtime/bin/native-media-probe.exe").Hash;ProducerOffsetVerified=$false} |
      ConvertTo-Json -Depth 5 | Set-Content "$evidence/result.json"
    Write-Output "Passed $unitChecks unit checks and $($cases.Count) offline media/bounds cases. Evidence: $evidence"
    exit 0
} catch { Write-Error "$_ Evidence: $evidence" -ErrorAction Continue; exit 1 }
