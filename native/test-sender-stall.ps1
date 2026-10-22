param([switch]$ReadAttribution,[switch]$Pacing,[ValidateSet("standard","low","legacy")][string]$DeveloperProfile,[switch]$BaselineOnly,[switch]$GopRejection,[switch]$Reconnect,[ValidateSet("retry-control","retry-once","retry-outage")][string]$RetryCase,[switch]$Recovery)
if($Recovery -and !$Reconnect){throw "Recovery requires Reconnect"}
if($RetryCase -and !$Reconnect){throw "RetryCase requires Reconnect"}
if($Reconnect){if($GopRejection){throw "Reconnect and GOP regression are separate fixtures"};if(!$DeveloperProfile){$DeveloperProfile="standard"}}
if($GopRejection -and (!$DeveloperProfile -or $DeveloperProfile -eq "legacy")){throw "GOP rejection regression requires a configured developer profile"}
if($DeveloperProfile){$Pacing=$true}
. "$PSScriptRoot/common.ps1"
$task=if($Recovery){'D9'}elseif($Reconnect){'D8'}elseif($GopRejection){'D7-GOP'}elseif($DeveloperProfile){'D7'}elseif($Pacing){'D4'}elseif($ReadAttribution){'D3'}else{'D2'}
$root=[IO.Path]::GetFullPath((Join-Path $Private ('sender-tests/'+$task)))
$evidence=Join-Path $root ('run-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $evidence | Out-Null
if($Recovery){Set-Content "$evidence/recovery-required.txt" "Maintained SDK must create replacement upload; no fixture restart"}
try {
    $sdk=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'private/build-producer-flat'))
    $runtime=Join-Path $Private $(if($Reconnect){'runtime-C2-D7-gop-final'}else{'runtime-C2-C7-final'})
    $runner=if($Recovery){Join-Path $root 'build-harness/PlaytestRecordingNativeProof.dll'}elseif($Reconnect){Join-Path $runtime 'bin/PlaytestRecordingNativeProof.dll'}elseif($DeveloperProfile){Join-Path $root 'build-harness/PlaytestRecordingNativeProof.dll'}else{Join-Path $runtime 'bin/PlaytestRecordingNativeProof.dll'}
    $tool=Get-NativeToolchain
    $envs=$tool.Environment
    $gst=Join-Path $Private 'gstreamer/{app}';$deps=Join-Path $Private 'deps'
    $envs.PATH="$gst/bin;"+$envs.PATH
    $envs.PKG_CONFIG_PATH="$gst/lib/pkgconfig";$envs.PKG_CONFIG_LIBDIR="$gst/lib/pkgconfig"
    if($Pacing -and !$GopRejection -and !$Reconnect) {
        & "$PSScriptRoot/apply-credentials-patch.ps1"
        if($LASTEXITCODE){throw 'Pinned SDK patch validation failed'}
        $sdk=Join-Path $root 'build-sdk'
        $args=@('-S',"$Private/source/cpp",'-B',$sdk,'-G','NMake Makefiles','-DCMAKE_BUILD_TYPE=Release',
            "-DSBREC_SOURCE_DIR=$($PSScriptRoot.Replace('\','/'))",'-DBUILD_DEPENDENCIES=OFF','-DBUILD_GSTREAMER_PLUGIN=ON','-DBUILD_TEST=OFF',
            "-DCMAKE_PREFIX_PATH=$gst;$deps","-DOPEN_SRC_INSTALL_PREFIX=$deps","-DPKG_CONFIG_EXECUTABLE=$gst/bin/pkg-config.exe",
            "-DCURL_LIBRARY=$gst/lib/curl.lib","-DOPENSSL_SSL_LIBRARY=$gst/lib/ssl.lib","-DOPENSSL_CRYPTO_LIBRARY=$gst/lib/crypto.lib")
        $r=Invoke-BoundedProcess $tool.CMake $args $PSScriptRoot "$evidence/sdk-configure.log" 120 $envs
        if($r.ExitCode){throw 'Candidate SDK configure failed'}
        $r=Invoke-BoundedProcess $tool.CMake @('--build',$sdk,'--target','gstkvssink','gstkvssink-offline') $PSScriptRoot "$evidence/sdk-build.log" 300 $envs
        if($r.ExitCode){throw 'Candidate SDK build failed'}

    } elseif(!$DeveloperProfile -and (Test-Path "$Private/source/c/src/source/SbrecSenderPacing.h")) {
        throw 'Legacy cached SDK predates the internal pacing layout. Use -Pacing to build the isolated candidate; old evidence is preserved.'
    }
    if($DeveloperProfile){
        $sdk=if($GopRejection -or $Reconnect){Join-Path $root '../D7/build-sdk'}else{Join-Path $root 'build-sdk'}
        Set-Content "$evidence/profile.txt" $DeveloperProfile
    }
    if($DeveloperProfile -and (!$Reconnect -or $Recovery)) {
        # Build only the runner in this task's isolated cache; never install over history.
        $harness=Join-Path $root 'build-harness'
        $r=Invoke-BoundedProcess $tool.CMake @('-S',$PSScriptRoot,'-B',$harness,'-G','NMake Makefiles','-DCMAKE_BUILD_TYPE=Release',"-DPKG_CONFIG_EXECUTABLE=$gst/bin/pkg-config.exe") $PSScriptRoot "$evidence/runner-configure.log" 90 $envs
        if($r.ExitCode){throw 'Candidate runner configure failed'}
        $r=Invoke-BoundedProcess $tool.CMake @('--build',$harness,'--target','PlaytestRecordingNativeProof') $PSScriptRoot "$evidence/runner-build.log" 180 $envs
        if($r.ExitCode){throw 'Candidate runner build failed'}
    }
    $compiler=Join-Path $envs.VCToolsInstallDir 'bin/Hostx64/x64/cl.exe'
    $linker=Join-Path $envs.VCToolsInstallDir 'bin/Hostx64/x64/link.exe'
    $offline=Join-Path $sdk 'CMakeFiles/gstkvssink-offline.dir'
    $flags=Get-Content "$offline/flags.make"
    $includeLine=($flags | Where-Object {$_ -like 'C_INCLUDES = *'}).Substring(13)
    $includes=@([regex]::Matches($includeLine,'-I(\S+)') | ForEach-Object {'/I'+$_.Groups[1].Value})
    $plugins=Join-Path $evidence 'plugins'
    New-Item -ItemType Directory $plugins | Out-Null
    Get-ChildItem "$runtime/plugins" -File | Where-Object Name -ne 'gstkvssink.dll' | Copy-Item -Destination $plugins
    $compileArgs=@('/nologo','/c','/TC','/MD','/O2','/DWIN32','/D_WINDOWS','/DSBREC_TEST_TRANSPORT=1','/D_CRT_SECURE_NO_WARNINGS')+$includes+@("$PSScriptRoot/tests/runner-response-offline.c","/Fo$evidence/transport.obj")
    $r=Invoke-BoundedProcess $compiler $compileArgs $evidence "$evidence/compile-transport.log" 45 $envs
    if($r.ExitCode){throw 'Offline transport compilation failed'}
    $objects=(Get-Content "$offline/objects1.rsp" -Raw).Trim() -split '\s+'
    $objectPaths=@($objects | Where-Object {$_ -notlike '*runner-response-offline.c.obj'} | ForEach-Object {Join-Path $sdk $_})
    $linkLine=(Get-Content "$offline/build.make" | Where-Object {$_ -match '^ /out:offline'}).Trim()
    if(!$linkLine){throw 'Expected accepted C7 offline link recipe unavailable'}
    $linkArgs=@(($linkLine -replace '/out:\S+|/implib:\S+|/pdb:\S+','').Trim() -split '\s+')
    $linkArgs+=@('/nologo','/MANIFEST:EMBED',"/out:$plugins/gstkvssink.dll","/implib:$evidence/gstkvssink.lib","/pdb:$evidence/gstkvssink.pdb")+$objectPaths+@("$evidence/transport.obj")
    if($Reconnect){
        # Only the existing termination call is aliased for observation. Accepted
        # SDK libraries/runner remain read-only; no recovery result is replaced.
        $callbackFlags=Get-Content "$sdk/c/CMakeFiles/cproducer.dir/flags.make"
        $callbackLine=($callbackFlags | Where-Object {$_ -like 'C_INCLUDES = *'}).Substring(13)
        $callbackIncludes=@([regex]::Matches($callbackLine,'-I(\S+)') | ForEach-Object {'/I'+$_.Groups[1].Value})
        $r=Invoke-BoundedProcess $compiler (@('/nologo','/c','/TC','/MD','/O2','/DNDEBUG','/DWIN32','/D_WINDOWS','/D_CRT_NONSTDC_NO_WARNINGS','/D_CRT_SECURE_NO_WARNINGS','/DkinesisVideoStreamTerminated=sbrec_test_stream_terminated',"$Private/source/c/src/source/CurlApiCallbacks.c","/Fo$evidence/callbacks.obj")+$callbackIncludes) $evidence "$evidence/compile-callback-observer.log" 45 $envs
        if($r.ExitCode){throw 'Termination observation compilation failed'}
        $linkArgs+=@("$evidence/callbacks.obj")
        Set-Content "$evidence/termination-observer.txt" "Actual SDK entry forwarded unchanged; no recovery override"
    }
    $r=Invoke-BoundedProcess $linker $linkArgs $sdk "$evidence/link-plugin.log" 45 $envs
    if($r.ExitCode){throw 'Offline plugin link failed'}
    $r=Invoke-BoundedProcess $compiler (@('/nologo','/MD','/O2','/EHsc','/std:c++17','/DNOMINMAX',"$PSScriptRoot/tests/native-sender-stall.cpp","/Fo$evidence/stall.obj","/Fe$evidence/native-sender-stall.exe")+$includes) $evidence "$evidence/compile-test.log" 45 $envs
    if($r.ExitCode){throw 'Generated fixture compilation failed'}
    if($Pacing) {
        $r=Invoke-BoundedProcess $compiler (@('/nologo','/MD','/O2','/EHsc','/std:c++17','/DNOMINMAX',"$PSScriptRoot/tests/native-sender-pacing.cpp","/Fo$evidence/policy.obj","/Fe$evidence/native-sender-pacing.exe")+$includes) $evidence "$evidence/compile-policy.log" 45 $envs
        if($r.ExitCode){throw 'Policy test compilation failed'}
        $r=Invoke-BoundedProcess "$evidence/native-sender-pacing.exe" @() $evidence "$evidence/policy.json" 5 $envs -MinimalEnvironment
        if($r.ExitCode){throw 'Focused policy checks failed'}
    }
    if($DeveloperProfile) {
        $r=Invoke-BoundedProcess $compiler (@('/nologo','/MD','/O2','/EHsc','/std:c++17','/DNOMINMAX',"$PSScriptRoot/tests/native-media-config.cpp","/Fo$evidence/config.obj","/Fe$evidence/native-media-config.exe")+$includes) $evidence "$evidence/compile-config.log" 45 $envs
        if($r.ExitCode){throw 'CREATED config test compilation failed'}
        $configEnv=Get-PrivateRuntimeEnvironment $runtime $evidence -Region 'eu-west-1'
        $r=Invoke-BoundedProcess "$evidence/native-media-config.exe" @($runner) $evidence "$evidence/config.json" 5 $configEnv -MinimalEnvironment
        if($r.ExitCode){throw 'CREATED-only config checks failed'}
    }
    $inputs=@($runner,"$Private/runtime/bin/native-media-probe.exe","$offline/flags.make","$offline/build.make","$offline/objects1.rsp")+$objectPaths
    if($Reconnect){
        $inputs+=@("$sdk/c/CMakeFiles/cproducer.dir/flags.make","$Private/source/c/src/source/CurlApiCallbacks.c","$Private/source/c/src/source/Response.c","$Private/source/c/src/source/SbrecSenderPacing.h")
        $inputs+=@(Get-ChildItem $sdk -Recurse -File -Filter '*.lib' | ForEach-Object {$_.FullName})
    }
    $inputs | ForEach-Object {[pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash}} | ConvertTo-Json | Set-Content "$evidence/build-inputs.json"
    $results=@()
    $cases=if($Reconnect){if($RetryCase){@($RetryCase)}elseif($BaselineOnly){@('retry-control')}else{@('retry-control','retry-once','retry-outage')}}elseif($BaselineOnly){@('baseline-noack')}elseif($Pacing){@('baseline-noack','stall-noack','stop-wait')}elseif($ReadAttribution){@('baseline','stall','baseline-noack','stall-noack')}else{@('baseline','stall','timeout')}
    foreach($case in $cases) {
        $dir=Join-Path $evidence $case
        New-Item -ItemType Directory $dir | Out-Null
        $runEnv=Get-PrivateRuntimeEnvironment $runtime $dir -Region 'eu-west-1'
        $runEnv.GST_PLUGIN_SYSTEM_PATH=$plugins;$runEnv.GST_PLUGIN_SYSTEM_PATH_1_0=$plugins
        $runEnv.GST_DEBUG='0'
        $r=Invoke-BoundedProcess "$evidence/native-sender-stall.exe" (@($runner,"$plugins/gstkvssink.dll","$dir/media.mkv","$runtime/certs/cert.pem",$case)+@(if($DeveloperProfile){$DeveloperProfile})+@(if($GopRejection){"reject-gop-first"})) $dir "$dir/fixture.log" 28 $runEnv -MinimalEnvironment
        $r | ConvertTo-Json -Depth 5 | Set-Content "$dir/process.json"
        $text=Get-Content "$dir/fixture.log" -Raw
        foreach($secret in @('OFFLINEONLYACCESS','OFFLINEONLYSECRET','OFFLINEONLYSESSION')) {if($text.Contains($secret)){throw 'Fake fixture value leaked'}}
        if($r.ExitCode){throw "Fixture failed: $case (exit $($r.ExitCode))"}
        $line=[regex]::Match($text,'\{"case":[^\r\n]+\}').Value
        if(!$line){throw 'Missing finite fixture receipt'}
        $receipt=$line | ConvertFrom-Json
        $receipt | ConvertTo-Json | Set-Content "$dir/receipt.json"
        if($Reconnect){
            # Attempts are independent MKV files, including undecodable short prefixes.
            # Keep every decoder outcome; a truncated attempt must not hide SDK retry facts.
            foreach($media in Get-ChildItem "$dir/media.mkv.attempt-*.mkv"){
                $d=Invoke-BoundedProcess "$Private/runtime/bin/native-media-probe.exe" @($media.FullName,"$($media.FullName).decode.json",'4096','15000','2097152') $dir "$($media.FullName).decode.log" 18 $runEnv -MinimalEnvironment
                $d | ConvertTo-Json -Depth 5 | Set-Content "$($media.FullName).decode-process.json"
            }
        } elseif($case -notin @('timeout','stop-wait')) {
            $d=Invoke-BoundedProcess "$Private/runtime/bin/native-media-probe.exe" @("$dir/media.mkv","$dir/decode.json",'4096','15000','2097152') $dir "$dir/decode.log" 18 $runEnv -MinimalEnvironment
            $d | ConvertTo-Json -Depth 5 | Set-Content "$dir/decode-process.json"
            if($d.ExitCode){throw "Decode failed: $case"}
        }
        $results+=@{Case=$case;Process=$r;Receipt=$receipt}
        Write-Output "Completed offline $case."
    }
    & python "$PSScriptRoot/tests/analyze-sender-stall.py" $evidence
    if($LASTEXITCODE){throw 'Finite transport measurement validation failed'}
    if($Reconnect){
        foreach($case in $cases){
            $dir=Join-Path $evidence $case;$runEnv=Get-PrivateRuntimeEnvironment $runtime $dir -Region 'eu-west-1';$points=Get-Content "$dir/selected-pts.txt"
            foreach($media in Get-ChildItem "$dir/media.mkv.attempt-*.mkv"){
                $d=Invoke-BoundedProcess "$Private/runtime/bin/native-media-probe.exe" @($media.FullName,"$($media.FullName).selected.json",'4096','15000','2097152','--select-video-pts-ns',$points,'--tolerance-ns','1000000') $dir "$($media.FullName).selected.log" 18 $runEnv -MinimalEnvironment
                $d | ConvertTo-Json -Depth 5 | Set-Content "$($media.FullName).selected-process.json"
            }
        }
    }
    foreach($case in @($cases | Where-Object {$_ -like 'stall*'})) {
        $dir=Join-Path $evidence $case
        $runEnv=Get-PrivateRuntimeEnvironment $runtime $dir -Region 'eu-west-1'
        $runEnv.GST_DEBUG='0'
        $points=Get-Content "$dir/selected-pts.txt"
        $d=Invoke-BoundedProcess "$Private/runtime/bin/native-media-probe.exe" @("$dir/media.mkv","$dir/selected-decode.json",'4096','15000','2097152','--select-video-pts-ns',$points,'--tolerance-ns','1000000') $dir "$dir/selected-decode.log" 18 $runEnv -MinimalEnvironment
        $d | ConvertTo-Json -Depth 5 | Set-Content "$dir/selected-decode-process.json"
        if($d.ExitCode){throw 'Selected-frame decode failed'}
    }
    & python "$PSScriptRoot/tests/analyze-sender-stall.py" $evidence
    if($LASTEXITCODE){throw 'Selected-frame validation failed'}
    @{Cases=$results;Evidence=$evidence;NoNetwork=$true;OpenUploadStallNotTCPReconnect=(!$Reconnect);InterceptedRetryNotTCPReconnect=[bool]$Reconnect} | ConvertTo-Json -Depth 8 | Set-Content "$evidence/results.json"
    Set-Content "$root/latest.txt" $evidence
    Write-Output "Evidence: $evidence"
} catch {
    Set-Content "$evidence/failure.txt" $_.Exception.Message
    Write-Error "$_ Evidence: $evidence" -ErrorAction Continue
    exit 1
}
