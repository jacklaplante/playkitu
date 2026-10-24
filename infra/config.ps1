$ErrorActionPreference='Stop'
function Read-InfrastructureConfiguration([string]$Path) {
    if(!$Path -or !(Test-Path -LiteralPath $Path) -or (Get-Item -LiteralPath $Path).Length -gt 32768){throw 'Explicit bounded owner configuration file required.'}
    $v=Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    $keys=@('awsAccountId','awsRegion','steamAppId','project','rolePath','alertEmail','identityPrefix','identityDomain')
    if(@($v.Keys | Where-Object {$_ -notin ($keys+@('videoStreamCount','streamMode','recordingLimits','lambdaConcurrency','apiRequestsPerSecond','apiBurst'))}).Count -or @($keys | Where-Object {!$v.ContainsKey($_)}).Count){throw 'Only nonsecret deployment fields are allowed.'}
    $mode=if($v.ContainsKey('streamMode')){$v.streamMode}else{'pooled'}
    if($mode -cnotin @('pooled','onDemand')){throw 'streamMode must be pooled or onDemand.'}
    $streams=if($v.ContainsKey('videoStreamCount')){$v.videoStreamCount}elseif($mode-ceq'onDemand'){0}else{4}
    if(($streams -isnot [int] -and $streams -isnot [long]) -or $streams -lt $(if($mode-ceq'onDemand'){0}else{1}) -or $streams -gt 8){throw 'Invalid retained video stream count.'}
    $limits=@{concurrentSessions=$null;admissionsPerDay=$null;admissionsPerMonth=$null;testerAdmissionsPerDay=$null;admissionAttemptsPerMinute=120;sessionRequestsPerMinute=30}
    if($v.ContainsKey('recordingLimits')){
        if($mode-cne'onDemand' -or $v.recordingLimits-isnot[System.Collections.IDictionary]){throw 'recordingLimits requires onDemand mode and an object.'}
        foreach($key in $v.recordingLimits.Keys){
            if(!$limits.ContainsKey($key)){throw 'Unknown recording limit.'}
            $value=$v.recordingLimits[$key]
            $rate=$key-in@('admissionAttemptsPerMinute','sessionRequestsPerMinute')
            $max=if($key-eq'admissionAttemptsPerMinute'){3000}elseif($key-eq'sessionRequestsPerMinute'){120}else{100000000}
            if(($null-eq$value -and $rate) -or ($null-ne$value -and (($value-isnot[int]-and$value-isnot[long])-or$value-lt 1-or$value-gt$max))){throw 'Limits require positive bounded integers, or null for count caps.'}
            $limits[$key]=$value
        }
    }
    $concurrency=if($v.ContainsKey('lambdaConcurrency')){$v.lambdaConcurrency}elseif($mode-ceq'onDemand'){$null}else{2}
    if($null-ne$concurrency-and(($concurrency-isnot[int]-and$concurrency-isnot[long])-or$concurrency-lt 1-or$concurrency-gt 100000)){throw 'Invalid Lambda concurrency.'}
    $apiRate=if($v.ContainsKey('apiRequestsPerSecond')){$v.apiRequestsPerSecond}elseif($mode-ceq'onDemand'){100}else{3}
    $apiBurst=if($v.ContainsKey('apiBurst')){$v.apiBurst}elseif($mode-ceq'onDemand'){200}else{6}
    foreach($value in @($apiRate,$apiBurst)){if(($value-isnot[int]-and$value-isnot[long])-or$value-lt 1-or$value-gt 10000){throw 'Invalid API rate/burst.'}}
    if($v.awsAccountId -notmatch '\A[0-9]{12}\z' -or $v.awsRegion -notmatch '\A[a-z]{2}-[a-z]+-[0-9]\z' -or
       $v.project -notmatch '\A[a-z][a-z0-9-]{1,23}\z' -or $v.rolePath -notmatch '\A/[A-Za-z0-9_/-]+/\z' -or
       ($v.steamAppId -isnot [int] -and $v.steamAppId -isnot [long]) -or $v.steamAppId -le 0 -or $v.steamAppId -gt [int]::MaxValue -or $v.alertEmail -notmatch '\A[^@ \r\n]+@[^@ \r\n]+\z' -or
       $v.identityPrefix -notmatch '\A[a-z0-9]{6}:\z' -or $v.identityDomain -notmatch '\A[a-z0-9-]{1,64}:\z') {throw 'Invalid owner trust configuration.'}
    $state="$($v.project)-tfstate-$($v.awsAccountId)-$($v.awsRegion)"
    if($state.Length -gt 63){throw 'Derived bucket name exceeds its fixed limit.'}
    [pscustomobject]@{account=$v.awsAccountId;region=$v.awsRegion;project=$v.project;steam_app_id=$v.steamAppId;video_stream_count=$streams;
        stream_mode=$mode;recording_limits=$limits;lambda_concurrency=$concurrency;api_rate=$apiRate;api_burst=$apiBurst;
        role_path=$v.rolePath;alert_email=$v.alertEmail;identity_prefix=$v.identityPrefix;identity_domain=$v.identityDomain;
        state_bucket=$state;recording_bucket="$($v.project)-$($v.awsAccountId)-$($v.awsRegion)";
        table="$($v.project)-sessions";function="$($v.project)-issuer";api="$($v.project)-api";
        schedule="$($v.project)-daily-cleanup";topic="$($v.project)-alerts";budget="$($v.project)-monthly";
        secret="$($v.project)/publisher-key";roles=@('issuer','media','deploy','retrieval','operations');
        boundary_name="$($v.project)-boundary";seed_role="$($v.project)-bootstrap";stream_name_prefix="$($v.project)-slot-"}
}
