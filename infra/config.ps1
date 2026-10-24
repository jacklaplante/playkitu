$ErrorActionPreference='Stop'
function Read-InfrastructureConfiguration([string]$Path) {
    if(!$Path -or !(Test-Path -LiteralPath $Path) -or (Get-Item -LiteralPath $Path).Length -gt 32768){throw 'Explicit bounded owner configuration file required.'}
    $v=Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    $keys=@('awsAccountId','awsRegion','steamAppId','project','rolePath','alertEmail','identityPrefix','identityDomain')
    if(@($v.Keys | Where-Object {$_ -notin ($keys+@('videoStreamCount'))}).Count -or @($keys | Where-Object {!$v.ContainsKey($_)}).Count){throw 'Only nonsecret deployment fields are allowed.'}
    $streams=if($v.ContainsKey('videoStreamCount')){$v.videoStreamCount}else{4}
    if(($streams -isnot [int] -and $streams -isnot [long]) -or $streams -lt 1 -or $streams -gt 8){throw 'videoStreamCount must be an integer from 1 to 8.'}
    if($v.awsAccountId -notmatch '\A[0-9]{12}\z' -or $v.awsRegion -notmatch '\A[a-z]{2}-[a-z]+-[0-9]\z' -or
       $v.project -notmatch '\A[a-z][a-z0-9-]{1,23}\z' -or $v.rolePath -notmatch '\A/[A-Za-z0-9_/-]+/\z' -or
       ($v.steamAppId -isnot [int] -and $v.steamAppId -isnot [long]) -or $v.steamAppId -le 0 -or $v.steamAppId -gt [int]::MaxValue -or $v.alertEmail -notmatch '\A[^@ \r\n]+@[^@ \r\n]+\z' -or
       $v.identityPrefix -notmatch '\A[a-z0-9]{6}:\z' -or $v.identityDomain -notmatch '\A[a-z0-9-]{1,64}:\z') {throw 'Invalid owner trust configuration.'}
    $state="$($v.project)-tfstate-$($v.awsAccountId)-$($v.awsRegion)"
    if($state.Length -gt 63){throw 'Derived bucket name exceeds its fixed limit.'}
    [pscustomobject]@{account=$v.awsAccountId;region=$v.awsRegion;project=$v.project;steam_app_id=$v.steamAppId;video_stream_count=$streams;
        role_path=$v.rolePath;alert_email=$v.alertEmail;identity_prefix=$v.identityPrefix;identity_domain=$v.identityDomain;
        state_bucket=$state;recording_bucket="$($v.project)-$($v.awsAccountId)-$($v.awsRegion)";
        table="$($v.project)-sessions";function="$($v.project)-issuer";api="$($v.project)-api";
        schedule="$($v.project)-daily-cleanup";topic="$($v.project)-alerts";budget="$($v.project)-monthly";
        secret="$($v.project)/publisher-key";roles=@('issuer','media','deploy','retrieval','operations');
        boundary_name="$($v.project)-boundary";seed_role="$($v.project)-bootstrap";stream_name_prefix="$($v.project)-slot-"}
}
