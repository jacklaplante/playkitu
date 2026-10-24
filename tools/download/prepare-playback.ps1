param([Parameter(Mandatory)][string]$InputFile,[Parameter(Mandatory)][string]$Output,[string]$RuntimeDirectory='')
. "$PSScriptRoot/../../native/common.ps1"
$ErrorActionPreference='Stop'
try {
    if(!$RuntimeDirectory){$RuntimeDirectory=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../native/private/runtime'))}
    if(![IO.Path]::IsPathFullyQualified($RuntimeDirectory)){throw 'Explicit runtime must be absolute.'}
    $InputFile=[IO.Path]::GetFullPath($InputFile);$Output=[IO.Path]::GetFullPath($Output)
    if(!(Test-Path -LiteralPath $InputFile -PathType Leaf) -or (Get-Item -LiteralPath $InputFile).Length -gt 64MB -or (Test-Path -LiteralPath $Output)){throw 'Fresh bounded playback output required.'}
    $work=Join-Path $Private ('playback/'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $work -Force|Out-Null
    $envs=Get-PrivateRuntimeEnvironment $RuntimeDirectory $work -Region 'us-east-2'
    # The game runtime intentionally omits MP4 muxing. Use only this plugin from
    # the pinned, already-extracted developer SDK; do not change the game bundle.
    $sdk=Join-Path $Private 'gstreamer/{app}'
    $lock=Get-Content "$PSScriptRoot/../../native/dependencies.json" -Raw|ConvertFrom-Json
    $marker=Join-Path $Private 'gstreamer/extracted.ok'
    if((Test-Path -LiteralPath $marker) -and (Get-Content -LiteralPath $marker -Raw).Trim() -eq $lock.artifacts[0].sha256){
        $plugins=Join-Path $work 'plugins';New-Item -ItemType Directory $plugins|Out-Null
        Copy-Item -LiteralPath "$sdk/lib/gstreamer-1.0/gstisomp4.dll" -Destination $plugins
        $envs.GST_PLUGIN_PATH=$plugins;$envs.GST_PLUGIN_PATH_1_0=$plugins
        $envs.PATH="$sdk/bin;"+$envs.PATH
    }
    # Rewrap the recorded H.264/AAC tracks; no decode, re-encode, recording or
    # credentials. Originals stay immutable. Keep each retrieved part separate
    # rather than concealing missing fragments behind a stitched timeline.
    $pipeline=@('-q','-e','filesrc',('location="'+$InputFile.Replace('\','/')+'"'),'!','matroskademux','name=d',
        'd.video_0','!','queue','!','h264parse','!','mux.',
        'd.audio_0','!','queue','!','aacparse','!','mux.',
        'mp4mux','name=mux','faststart=true','!','filesink',('location="'+$Output.Replace('\','/')+'"'))
    $result=Invoke-BoundedProcess "$RuntimeDirectory/bin/gst-launch-1.0.exe" $pipeline $work "$work/remux.log" 35 $envs -MinimalEnvironment
    if($result.ExitCode -or !(Test-Path -LiteralPath $Output) -or (Get-Item -LiteralPath $Output).Length -eq 0){exit 3}
    exit 0
}catch{exit 3}
