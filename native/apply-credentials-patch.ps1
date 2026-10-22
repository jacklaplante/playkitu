param([string]$SourceRoot = '')
. "$PSScriptRoot/common.ps1"
try {
    $expected = [IO.Path]::GetFullPath((Join-Path $Private 'source/cpp'))
    if (!$SourceRoot) { $SourceRoot = $expected }
    if ([IO.Path]::GetFullPath($SourceRoot) -ne $expected -or (Get-Item -LiteralPath $SourceRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Patch target must be the task-owned pinned producer source' }
    # Git apply can silently skip absolute patch targets when launched from a
    # nested caller directory. Keep its prefix at this checkout's root; target
    # roots below remain fixed and every output hash is still checked.
    # git apply also works outside a repository: source archives need no .git.
    # Absolute --directory targets remain pinned below, never caller-selected.
    $gitOptions = @('-C', $Private, '-c', 'core.autocrlf=false')
    function Get-SourceHash([string]$Path) {
        # A manifest's null base hash denotes a new task-owned SDK source file.
        if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
        $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
    }
    $plans = @(
        @{ Root=$SourceRoot; Name='kvssink-memory-provider' },
        @{ Root=(Join-Path $Private 'source/c'); Name='producer-c-private-logging' }
    )
    # Check every full source hash and every patch before modifying either SDK.
    # Roots are fixed task-owned directories; no index/branch edits are performed.
    foreach ($plan in $plans) {
        if ((Get-Item -LiteralPath $plan.Root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'SDK source root is a reparse point' }
        $plan.Manifest = Get-Content "$PSScriptRoot/patches/$($plan.Name).json" -Raw | ConvertFrom-Json
        # Classify the complete file set, not each file independently: B2a left
        # three of B2c's five files at base. Accept only that exact known vector,
        # never an arbitrary mixture of base/old/current or a best-effort apply.
        $base = $true; $current = $true
        $previous = [bool]$plan.Manifest.previousPatch
        foreach ($file in $plan.Manifest.files) {
            $hash = Get-SourceHash (Join-Path $plan.Root $file.file)
            $base = $base -and $hash -eq $file.baseSha256
            $current = $current -and $hash -eq $file.patchedSha256
            $priorHash = if ($file.previousSha256) { $file.previousSha256 } else { $file.baseSha256 }
            $previous = $previous -and $hash -eq $priorHash
        }
        $plan.Pending = !$current
        if ($current -or $base) {
            $plan.Patch = Join-Path $PSScriptRoot "patches/$($plan.Name).patch"
        } elseif ($previous) {
            # Direct old->current patch retains all B2a changes and is preflighted
            # alongside the C SDK before either source tree can be modified.
            $plan.Patch = Join-Path $PSScriptRoot "patches/$($plan.Manifest.previousPatch)"
        } else { throw "Unexpected or mixed pinned patch state: $($plan.Name)" }
        if ($plan.Pending) {
            & git @gitOptions apply --check --ignore-space-change --unsafe-paths "--directory=$($plan.Root)" $plan.Patch
            if ($LASTEXITCODE) { throw "Patch check failed: $($plan.Name)" }
        }
    }
    foreach ($plan in $plans) {
        if ($plan.Pending) {
            & git @gitOptions apply --ignore-space-change --unsafe-paths "--directory=$($plan.Root)" $plan.Patch
            if ($LASTEXITCODE) { throw "Patch apply failed: $($plan.Name)" }
        }
        foreach ($file in $plan.Manifest.files) {
            if ((Get-SourceHash (Join-Path $plan.Root $file.file)) -ne $file.patchedSha256) { throw "Patch output hash mismatch: $($file.file)" }
        }
    }
    Write-Output 'Pinned credential and SDK private-logging patches verified (canonical LF source)'
    exit 0
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
