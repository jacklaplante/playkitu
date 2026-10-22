param([Parameter(Mandatory)][string]$InputFile,[string]$Output='',
    [ValidateRange(1,4096)][int]$SpanLimit=4096,
    [ValidateRange(100,30000)][int]$TimeoutMs=30000,
    [ValidateRange(1024,2097152)][int]$OutputBytes=2097152,
    [string[]]$SelectedVideoPtsNs=@(),[ValidateRange(0,1000000)][int]$ToleranceNs=1000000,
    [string]$RuntimeDirectory='')
. "$PSScriptRoot/common.ps1"
try {
    $evidence=Join-Path $Private "media-probes/$([guid]::NewGuid())"
    New-Item -ItemType Directory $evidence | Out-Null
    $InputFile=[IO.Path]::GetFullPath($InputFile)
    if(!$Output) { $Output=Join-Path $evidence 'probe.json' }
    $Output=[IO.Path]::GetFullPath($Output)
    if(Test-Path -LiteralPath $Output) { throw 'Probe output must be a new file' }
    # Offline owner-probe candidates can be checked before promotion, preserving
    # the accepted runtime and all delivered gameplay recorder binaries.
    $runtime=if($RuntimeDirectory){[IO.Path]::GetFullPath($RuntimeDirectory)}else{Join-Path $Private 'runtime'}
    $envs=Get-PrivateRuntimeEnvironment $runtime $evidence -Region 'eu-west-1'
    $envs.GST_DEBUG='0'
    $nativeArgs=@($InputFile,$Output,"$SpanLimit","$TimeoutMs","$OutputBytes")
    if($SelectedVideoPtsNs.Count) {
        if($SelectedVideoPtsNs.Count -gt 64){throw 'At most 64 selected video timestamps'}
        foreach($value in $SelectedVideoPtsNs){if($value -notmatch '^\d{1,19}$' -or [bigint]$value -gt [long]::MaxValue){throw 'Exact nonnegative signed-range nanoseconds required'}}
        $nativeArgs+=@('--select-video-pts-ns',($SelectedVideoPtsNs -join ','),'--tolerance-ns',"$ToleranceNs")
    }
    $r=Invoke-BoundedProcess "$runtime/bin/native-media-probe.exe" $nativeArgs $evidence "$evidence/native.log" 35 $envs -MinimalEnvironment
    Write-Output "Exit $($r.ExitCode); probe: $Output; diagnostic evidence: $evidence"
    exit $r.ExitCode
} catch { Write-Error $_ -ErrorAction Continue; exit 2 }
