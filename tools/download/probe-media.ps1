param([Parameter(Mandatory)][string]$InputFile,[Parameter(Mandatory)][string]$Output,[string[]]$SelectedVideoPtsNs=@(),[int]$ToleranceNs=1000000,[string]$RuntimeDirectory='')
# The canonical native wrapper supplies the isolated runtime and bounded decoder.
& "$PSScriptRoot/../../native/probe-media.ps1" @PSBoundParameters
exit $LASTEXITCODE
