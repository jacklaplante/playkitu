param([Parameter(Mandatory)][string]$Region,
    [ValidateSet('Probe', 'Encode', 'Decode')][string]$Mode = 'Probe',
    [string]$Runtime = '', [string]$Evidence = '',
    [string]$Output = '', [string]$InputFile = '',
    [int]$Seconds = 5, [int]$Width = 1280, [int]$Height = 720, [int]$Fps = 30,
    [int]$TimeoutSeconds = 90
)
. "$PSScriptRoot/common.ps1"
$code = 2
$phase = 'validation'
$result = $null
$r = $null
try {
    if (!$Runtime) { $Runtime = Join-Path $Private 'runtime' }
    if (!$Evidence) { $Evidence = Join-Path $Private "runs/$([guid]::NewGuid())" }
    $Runtime = [IO.Path]::GetFullPath($Runtime)
    $Evidence = [IO.Path]::GetFullPath($Evidence)
    if ($Evidence.StartsWith('\\') -or $Runtime.StartsWith('\\') -or $TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 120) { throw 'Local paths and timeout 1..120 required' }
    New-Item -ItemType Directory $Evidence -Force | Out-Null
    # New registries and an empty, different working directory are used every
    # time. A previous plugin cache cannot hide a missing packaged plugin.
    $cwd = Join-Path $Evidence "cwd-$([guid]::NewGuid())"
    New-Item -ItemType Directory $cwd | Out-Null
    $exe = Join-Path $Runtime 'bin/native-proof.exe'
    if (!(Test-Path -LiteralPath $exe) -or !(Test-Path "$Runtime/bin/gstreamer-1.0-0.dll")) { $code = 3; throw 'Private runtime missing' }
    $envs = Get-PrivateRuntimeEnvironment $Runtime $cwd -Region $Region
    switch ($Mode) {
        'Probe' { $args = @('probe') }
        'Encode' {
            if (!$Output) { $Output = Join-Path $Evidence 'synthetic.mkv' }
            $Output = [IO.Path]::GetFullPath($Output)
            if ($Output.StartsWith('\\') -or (Test-Path -LiteralPath $Output)) { throw 'Output must be a new local file' }
            if ($Seconds -lt 1 -or $Seconds -gt 60 -or $Width -lt 64 -or $Width -gt 1280 -or $Height -lt 64 -or $Height -gt 720 -or $Width % 2 -or $Height % 2 -or $Fps -lt 1 -or $Fps -gt 30) { throw 'Invalid encoding settings' }
            $temporaryOutput = Join-Path $cwd 'synthetic.mkv'
            $args = @('encode', $temporaryOutput, "$Seconds", "$Width", "$Height", "$Fps")
        }
        'Decode' {
            if (!$InputFile) { throw 'InputFile required' }
            $InputFile = [IO.Path]::GetFullPath($InputFile)
            if ($InputFile.StartsWith('\\') -or !(Test-Path -LiteralPath $InputFile -PathType Leaf) -or (Get-Item -LiteralPath $InputFile).Length -gt 32MB) { throw 'Local synthetic input <=32 MiB required' }
            $args = @('decode', $InputFile)
        }
    }
    $phase = 'native invocation'
    $r = Invoke-BoundedProcess $exe $args $cwd "$Evidence/native.log" $TimeoutSeconds $envs
    $code = $r.ExitCode
    $result = [ordered]@{ Schema=1; Mode=$Mode; ExitCode=$code; NativeExitCode=$r.ExitCode; WallSeconds=$r.Seconds; PeakWorkingSetBytes=$r.PeakWorkingSetBytes; CpuSeconds=$r.CpuSeconds; Runtime=$Runtime; WorkingDirectory=$cwd; NativeLog=$r.Log; SampledModules=$r.Modules }
    $metrics = @{}
    foreach ($line in Get-Content -LiteralPath $r.Log) { if ($line -match '^([a-z0-9_]+)=(.*)$') { $metrics[$Matches[1]]=$Matches[2] } }
    $result.Metrics = $metrics
    # Native CREATE_NEW owns successful output reservation. A timeout may kill
    # the native cleanup, so remove only the new path this run was asked to create.
    if ($Mode -eq 'Encode') {
        $phase = 'output publication'
        if (!$code) { Move-Item -LiteralPath $temporaryOutput -Destination $Output -ErrorAction Stop }
        elseif (Test-Path -LiteralPath $temporaryOutput) { Remove-Item -LiteralPath $temporaryOutput -Force }
    }
    if ($Mode -eq 'Encode' -and !$code) { $result.Output=$Output; $result.OutputBytes=(Get-Item -LiteralPath $Output).Length }
    $phase = 'result evidence'
    $result | ConvertTo-Json -Depth 5 | Set-Content "$Evidence/result.json"
    Write-Output "Exit $code; evidence: $Evidence/result.json"
    exit $code
} catch {
    $failure = $_
    # Encoding success is only a native result. Publication/evidence failures
    # must fail the wrapper even when the native encoder returned zero.
    if ($phase -ne 'validation') { $code = 5 }
    if (!$result) { $result = [ordered]@{ Schema=1; Mode=$Mode; Runtime=$Runtime } }
    $result.ExitCode = $code
    $result.FailurePhase = $phase
    $result.Error = $failure.Exception.Message
    if ($Mode -eq 'Encode') {
        $result.RequestedOutput = $Output
        # Keep the successfully encoded temporary file as diagnostic evidence
        # after failed publication; never describe it as the published output.
        if ($temporaryOutput -and (Test-Path -LiteralPath $temporaryOutput)) { $result.RetainedMediaPath = $temporaryOutput }
    }
    if ($Evidence -and (Test-Path -LiteralPath $Evidence -PathType Container)) {
        try { $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$Evidence/result.json" }
        catch { Write-Error "Could not save failure evidence: $($_.Exception.Message)" -ErrorAction Continue }
    }
    Write-Error $failure -ErrorAction Continue
    exit $code
}
