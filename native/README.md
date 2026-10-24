# Native recording engine

Windows x64 source and local development tooling for Playkitu. It builds the
in-process recording DLL, patched AWS Kinesis `kvssink` plugin, bounded decoded
media probe and offline tests. No Unity project, game assets, Steam installation,
account credentials or old dependency cache is needed to compile it.

## Build from a fresh clone or source archive

Install PowerShell 7, Git (patch application only; `.git` is not required), and
Visual Studio C++ x64 tools with a Windows SDK and the bundled CMake component.
CMake must be at least 3.20. Then run:
C# loader regression tests additionally use a .NET 6 SDK/runtime test host; this
host is not packaged into the Unity player or native runtime.

```powershell
pwsh -NoProfile -File native/prepare.ps1
pwsh -NoProfile -File native/build-credentials.ps1
pwsh -NoProfile -File native/test.ps1
```

Run these sequentially. Downloads are public HTTPS and verified against
`dependencies.json`; installers are extracted, never executed. Preparation uses
explicit local SDK source paths instead of upstream automatic dependency fetches.
`prepare.ps1 -Offline` requires a complete previously downloaded, hash-verified
cache; `-SourcesOnly` stops before patching/building. Build parallelism is bounded
to two (NMake may build serially). No global PATH, installation, registry or
toolchain setting is changed. Outputs, downloads, logs and test media stay under
ignored `native/private/`. These paths may be large and are never source exports.

The local runtime is `native/private/runtime/`. `inventory.json` enumerates the
actual x64 PE dependency closure; `credential-provider.json` and
`synthetic-runner.json` bind source/patch hashes. Packaging verifies required
exports, rejects the offline SDK transport and checks every copied dependency.
The package is a **local development artifact, not an approved binary release**.
See [redistribution status](REDISTRIBUTION.md).

## Trust and compatibility

The C ABI layouts remain unchanged. `sbrec_runner_trusted_region_version()` must
return 1; the public managed loader rejects a missing/unknown capability and
releases its CREATED owner. The existing no-argument version delegate is reused
for IL2CPP. Native start reads only `PLAYREEL_AWS_REGION` via the Windows API,
validates its bounded ASCII spelling and copies it before credential/global
ownership or worker creation. There is no region default and no fallback to
ambient `AWS_REGION` or endpoint overrides. Unity's exclusive environment lease
sets the value from immutable developer trust and retains it through retirement.

The old `sbrec_` export names and DLL name are retained for ABI compatibility.
Those symbol names do not identify a deployment, account or trust policy. The
decoded probe's neutral schema kind is `playreel.decoded-media-probe`.

The SDK keeps its existing 1 ms AV ordering mode. Only the common UTC translation
offset is aligned to that grid; relative PTS/DTS and frame spacing are preserved.
Submitted observation uncertainty includes a conservative 1 ms UTC mapping bound.
Source-scheduling uncertainty is a different measurement and remains unchanged.

## Tests and local tools

`test.ps1` runs source patch checks, configuration, region/start ownership, clock
unit, credential/signing/redaction and lifecycle regressions without capture,
Steam, Unity, real credentials or cloud sockets. Its optional `-GeneratedMedia`
also runs the existing actual SDK interception with generated video/audio and
offline decoding. This needs the pinned Media Foundation/GStreamer encoders and
appropriate Windows hardware; it does not open a microphone or recording device.
The intercepted plugin and negative capability DLL are test-only and never enter
the runtime package. Results are JSON and logs in unique ignored directories.

For patch setup changes alone, run `pwsh -NoProfile -File native/test-patches.ps1`.
It uses small copies of the prepared SDK sources to check fresh Git checkouts,
archive layouts, repeated application, CRLF input and rejected-source integrity;
it does not rebuild the native runtime. Patching resolves the enclosing Git root
so Git cannot silently skip absolute SDK paths from a nested working directory.

`test-clock.ps1` exercises generated timing/selected-frame decode separately;
`test-lifecycle.ps1` includes a prior-defect negative control. Shared historical
synthetic fixtures live only in `../tests/fixtures/` and are not fresh runtime
evidence. Tests do not establish live recording, clean-machine support or physical
capture/log synchronization.

`run.ps1 -Region <trusted-region> -Mode Probe|Encode|Decode` is the bounded
generated-media console wrapper, not a cloud uploader. `probe-media.ps1` decodes
an explicitly supplied local media file. Neither selects physical capture input.
Public trust/account validation remains in the managed/service layers.
