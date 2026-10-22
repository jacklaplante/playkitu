# Development and validation

Use the smallest loop that answers the question: the minimal Unity project for package behavior, an existing Steam owner for authentication, native targets for media/ABI, and a player for IL2CPP/distribution.

## Local entry points

From the repository root with PowerShell 7:

~~~powershell
pwsh -NoProfile -File backend/issuer/prepare.ps1
pwsh -NoProfile -File backend/issuer/test.ps1
pwsh -NoProfile -File backend/issuer/package.ps1
pwsh -NoProfile -File tools/download/prepare.ps1
pwsh -NoProfile -File tools/download/test.ps1
pwsh -NoProfile -File infra/prepare.ps1
pwsh -NoProfile -File infra/test.ps1
~~~

These prepare tools and run offline checks; they do not deploy AWS. Each component's README describes prerequisites, receipts and optional inputs.

Open **samples/MinimalUnity** in Unity 2022.3.62f3 to run package EditMode/PlayMode tests. Unity is the authoritative client compile check; generated projects and standalone compilation are early checks.

Native:

~~~powershell
pwsh -NoProfile -File native/prepare.ps1
pwsh -NoProfile -File native/build-credentials.ps1
pwsh -NoProfile -File native/test.ps1 -GeneratedMedia
~~~

Generated-media cases use synthetic video/tone and intercepted SDK transport. They do not open microphones, record a desktop or contact AWS.

## Small Windows IL2CPP check

Close this sample's Editor. Choose fresh absolute output:

~~~powershell
$output = Join-Path $PWD '.artifacts/my-native-check'
pwsh -NoProfile -File samples/MinimalUnity/build-native-self-check.ps1 -Output $output
. ./native/common.ps1
$result = Invoke-BoundedProcess "$output/player/PlayreelSample.exe" @(
    '-batchmode', '-nographics', '--playreel-native-self-check',
    '-logFile', "$output/self-check-player.log"
) $PWD.Path "$output/self-check-process.log" 60 @{} -MinimalEnvironment
if ($result.ExitCode -ne 0) { throw 'Native self-check failed' }
Get-Content "$output/player/native-self-check.json"
~~~

The wrapper restores settings, bounds its process and refuses existing output. The opt-in player only loads a CREATED owner, checks capability/state/clock and releases it. It never calls SDK Start. Its receipt needs fresh writable developer output.

## Demonstrated checks

The October 22, 2026 extraction was checked with:

- Independent Unity import; 36 EditMode and 10 focused PlayMode lifecycle/gain cases.
- A private game-owned adapter compile and 20 focused Editor checks with preserved resources/configuration.
- Fresh pinned native dependencies, production package, 12 focused native suite cases and a lifecycle negative control.
- A real Windows IL2CPP native-load check, including execution from another working directory.
- Issuer/download suites, real decoder interoperability, explicit override checks, mocked Terraform and offline operations checks.
- Two identical Linux issuer ZIPs from the same inputs/toolchain.

These are scoped retained results, not promises about every configuration. Historical fixtures are labeled; they are not fresh live evidence. Failed checks were retained while causes were corrected.

## Separate release work

A newly deployed backend with a consenting Steam client, clean-machine support, continuous live coverage and binary redistribution clearance are not established by offline checks. A decoder's EOS alone does not prove complete capture.

Caches, native/player binaries, media, receipts, credential configuration and Terraform state stay in ignored output. Export only reviewed source and deliberately prepared release assets.
