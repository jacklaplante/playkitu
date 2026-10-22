# Public dependency downloads only; no AWS CLI, credentials, profiles or discovery.
$ErrorActionPreference = 'Stop'
$taskPrivate = Join-Path $PSScriptRoot 'private'
New-Item -ItemType Directory -Force $taskPrivate | Out-Null
$taskZip = Join-Path $taskPrivate 'terraform.zip'
$taskHash = '6ea2c186e0e76a113af30fbcdcf7a4b4c70756317994a04929e5c90b9e94b1df'
if (!(Test-Path -LiteralPath $taskZip) -or (Get-FileHash -LiteralPath $taskZip).Hash.ToLowerInvariant() -ne $taskHash) {
    Invoke-WebRequest 'https://releases.hashicorp.com/terraform/1.16.6/terraform_1.16.6_windows_amd64.zip' -OutFile $taskZip
}
if ((Get-FileHash -LiteralPath $taskZip).Hash.ToLowerInvariant() -ne $taskHash) { throw 'Terraform archive SHA256 mismatch.' }
Expand-Archive -LiteralPath $taskZip -DestinationPath (Join-Path $taskPrivate 'terraform') -Force
$taskTf = Join-Path $taskPrivate 'terraform/terraform.exe'
$env:CHECKPOINT_DISABLE = '1'
$env:TF_IN_AUTOMATION = '1'
$env:AWS_EC2_METADATA_DISABLED = 'true'
Write-Output 'Pinned Terraform ready. Run test.ps1 for backend-disabled initialization and mocked validation.'
