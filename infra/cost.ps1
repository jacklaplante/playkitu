param([Parameter(Mandatory)][ValidateRange(0,10000)][double]$IngestGb,
 [Parameter(Mandatory)][ValidateRange(0,10000)][double]$RetainedGbMonth,
 [Parameter(Mandatory)][ValidateRange(0,10000)][double]$RetrievedGb,
 [Parameter(Mandatory)][ValidateRange(0,10000)][double]$EgressGb,
 [Parameter(Mandatory)][ValidateRange(0,1000000)][double]$RequestThousands,
 [Parameter(Mandatory)][string]$ReviewedRatesFile)
# Inputs are current owner-reviewed region-specific rates, never a claimed live quote.
$r=Get-Content $ReviewedRatesFile -Raw | ConvertFrom-Json -AsHashtable
foreach($key in 'ingestPerGb','retainedPerGbMonth','retrievalPerGb','egressPerGb','perThousandRequests','backendEstimateUsd'){
 if(!$r.ContainsKey($key) -or $r[$key] -lt 0 -or [double]::IsNaN($r[$key]) -or [double]::IsInfinity($r[$key])){throw 'Explicit finite reviewed rates required.'}
}
[pscustomobject]@{EstimateUsd=$IngestGb*$r.ingestPerGb+$RetainedGbMonth*$r.retainedPerGbMonth+$RetrievedGb*$r.retrievalPerGb+$EgressGb*$r.egressPerGb+$RequestThousands*$r.perThousandRequests+$r.backendEstimateUsd;Meaning='Owner-input planning estimate only; excludes unspecified charges/taxes; delayed alerts are not spending caps.'}
