param([string]$OutputDirectory = '', [switch]$NoRestore)
. "$PSScriptRoot/environment.ps1"
Push-Location $taskRoot
try {
    # Candidate packaging must not replace the deployed/accepted ZIP before
    # independent review. The default keeps the established packaging interface.
    $taskOut = $taskPrivate
    if ($OutputDirectory) {
        if (![IO.Path]::IsPathFullyQualified($OutputDirectory) -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'Candidate output must be a fresh absolute directory.' }
        $taskOut = [IO.Path]::GetFullPath($OutputDirectory)
        New-Item -ItemType Directory -Path $taskOut | Out-Null
    }
    if (!$NoRestore) {
        & "$taskSdk/dotnet.exe" restore src/Issuer.csproj --runtime linux-x64 --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Publish restore failed.' }
    }
    $taskStage = Join-Path $taskPrivate ('publish-' + [Guid]::NewGuid().ToString('N'))
    & "$taskSdk/dotnet.exe" publish src/Issuer.csproj --no-restore -c Release -r linux-x64 --self-contained false -p:PublishReadyToRun=false -p:PublishTrimmed=false -p:DebugType=None -o $taskStage
    if ($LASTEXITCODE -ne 0) { throw 'Issuer publish failed.' }
    $taskArtifact = Join-Path $taskOut 'issuer.zip'
    # Stable ordering/timestamps make the ZIP reproducible for the same source and
    # locked packages; publish staging names and local file mtimes are not inputs.
    $taskZipStream = [IO.File]::Create($taskArtifact)
    $taskArchive = [IO.Compression.ZipArchive]::new($taskZipStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($taskFile in (Get-ChildItem -LiteralPath $taskStage -File -Recurse | Sort-Object FullName)) {
            $taskRelative = [IO.Path]::GetRelativePath($taskStage, $taskFile.FullName).Replace('\','/')
            $taskEntry = $taskArchive.CreateEntry($taskRelative, [IO.Compression.CompressionLevel]::Optimal)
            $taskEntry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $taskInput = [IO.File]::OpenRead($taskFile.FullName)
            $taskOutput = $taskEntry.Open()
            try { $taskInput.CopyTo($taskOutput) } finally { $taskOutput.Dispose(); $taskInput.Dispose() }
        }
    } finally { $taskArchive.Dispose(); $taskZipStream.Dispose() }
    $taskEntries = [IO.Compression.ZipFile]::OpenRead($taskArtifact)
    try {
        $taskNames = @($taskEntries.Entries.FullName)
        foreach($taskRequired in 'Playkitu.Issuer.dll','Playkitu.Issuer.deps.json','Playkitu.Issuer.runtimeconfig.json','Amazon.Lambda.Core.dll','AWSSDK.S3.dll','AWSSDK.DynamoDBv2.dll') {
            if ($taskRequired -notin $taskNames) { throw "Missing publish entry: $taskRequired" }
        }
        if ($taskNames | Where-Object { $_ -match 'Tests|NUnit|\.pdb$|\.ps1$|private/|^/' }) { throw 'Unexpected publish payload.' }
        $taskReceipt = if ($OutputDirectory) { Join-Path $taskOut 'artifact.json' } else { Join-Path $taskPrivate 'evidence/artifact.json' }
        [ordered]@{Sdk='10.0.402';Runtime='dotnet10';Architecture='x86_64';Handler='Playkitu.Issuer::Playkitu.Issuer.Function::HandleAsync';Sha256=(Get-FileHash -LiteralPath $taskArtifact).Hash;Bytes=(Get-Item -LiteralPath $taskArtifact).Length;Entries=$taskNames} |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $taskReceipt
    } finally { $taskEntries.Dispose() }
    Write-Output "Packaged $taskArtifact"
} finally { Pop-Location }
