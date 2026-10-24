param(
    [Parameter(Mandatory)][string]$ConfigurationFile,
    [bool]$AdmissionsEnabled = $false,
    [bool]$UploadsEnabled = $false,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot
. (Join-Path $taskRoot 'config.ps1')
$c = Read-InfrastructureConfiguration $ConfigurationFile
# The caller names the destination; tests never share owner-prepared controls.
$taskOut = [IO.Path]::GetFullPath($OutputDirectory, $PWD.ProviderPath)
New-Item -ItemType Directory -Force $taskOut | Out-Null
# Canonical state comes from the backend model, not a second hand-maintained seed.
$seedText=& pwsh -NoProfile -File (Join-Path $taskRoot '../backend/issuer/seed.ps1') -VideoStreams $c.video_stream_count
if($LASTEXITCODE){throw 'Canonical seed command failed.'}
$taskSeed=($seedText -join '') | ConvertFrom-Json -AsHashtable -DateKind String
if ($taskSeed.pk.S -ne 'CONTROL' -or $taskSeed.sk.S -ne 'META' -or $taskSeed.v.N -ne '1' -or $taskSeed.admissionsEnabled.BOOL -or $taskSeed.uploadsEnabled.BOOL -or ($taskSeed.data.S | ConvertFrom-Json).slots.Count -ne $c.video_stream_count) { throw 'Issuer seed changed; review required.' }
@{TableName=$c.table;Item=$taskSeed;ConditionExpression='attribute_not_exists(#pk)';ExpressionAttributeNames=@{'#pk'='pk'}} | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $taskOut 'control-seed.json')
@{TableName=$c.table;Key=@{pk=@{S='CONTROL'};sk=@{S='META'}};UpdateExpression='SET #ae = :ae, #ue = :ue';ConditionExpression='attribute_exists(#pk) AND attribute_exists(#sk)';ExpressionAttributeNames=@{'#pk'='pk';'#sk'='sk';'#ae'='admissionsEnabled';'#ue'='uploadsEnabled'};ExpressionAttributeValues=@{':ae'=@{BOOL=$AdmissionsEnabled};':ue'=@{BOOL=$UploadsEnabled}};ReturnValues='NONE'} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskOut 'control-switch.json')
# Strong readback exposes only the two switches, never CONTROL data/leases.
# Callers must require actual BOOL values: missing/null/string is not OFF.
@{TableName=$c.table;Key=@{pk=@{S='CONTROL'};sk=@{S='META'}};ConsistentRead=$true;ProjectionExpression='#ae, #ue';ExpressionAttributeNames=@{'#ae'='admissionsEnabled';'#ue'='uploadsEnabled'}} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $taskOut 'control-read.json')
Write-Output 'Prepared conditional seed and flag-only change; no AWS commands executed.'
