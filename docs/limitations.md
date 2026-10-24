# Current limits and release status

playkitu is experimental and source-first. These limits affect whether a tester
gets useful footage; they are not just implementation details. Defaults below
describe the maintained source, not the state of a particular AWS installation.

## Capacity and admission

| Limit | Current behavior | Changing it |
| --- | --- | --- |
| Video pool | Four streams by default; one leased stream per video session. Full or quarantined slots can leave an admitted session with logs only. There is no waiting room guaranteeing later video. | `videoStreamCount` supports 1–8. An existing installation needs a coordinated resize, not just a JSON edit. |
| Slot reuse | A released stream stays unavailable until 50 minutes after its last media credential expires. A fresh credential lasts 15 minutes, so reuse can be delayed roughly 65 minutes after an early close. | This protects against a previous client writing into the next session. It is source behavior, not a pool-size setting. |
| Active sessions | Eight active sessions globally, one per tester; pending admission is separately bounded. | Backend code and validation/load review. More streams alone do not raise this. |
| Daily/monthly quota | Eight successful admissions per tester per UTC day, eight globally per UTC day, fifty globally per UTC month. Closing or recording failure does not refund an admission. Logs-only admissions count too. | Backend code, deployment and policy review; these are not currently deployment JSON fields. |

Pool size is configurable; the system is **not automatically elastic**. The same
pool can remain full after testers have quit because of quarantine. See the
[capacity/migration guide](capacity.md), `backend/issuer/src/IssuerService.cs`,
`State.cs` and `Contracts.cs` before changing these rules.

## Capture and compatibility

- The recording client currently supports **Windows x86-64 only**, with Unity
  2022.3.62f3 as the validated baseline. Other Unity versions/platforms need proof.
  The native path depends on Windows Media Foundation/H.264 hardware encoding;
  universal GPU/driver compatibility or a software fallback is not established.
- Profiles currently cap video at **1280×720, 30 FPS and 2,000 kbps**, with 48 kHz
  stereo AAC at 96 kbps. This is not a full-resolution/lossless recorder.
- Recording is best effort and gameplay takes priority. Bounded queues, bandwidth
  limits, retries and shutdown can drop audio/video/logs or the final tail. There
  is no durable offline capture spool promising later recovery after long outages.
  Footage already persisted in AWS can remain after a crash.
- A session's media deadline is **four hours**; diagnostic finalization is bounded
  to another 60 seconds. Negotiated diagnostics have a 256 MiB stored-object and
  8,192-object ceiling. Heavy logging/retries can exhaust budgets before four hours.
  Duration support does not promise uninterrupted coverage or every log record.
- Steam app ownership and a valid Steam client/app context are required. The host
  owns Steam initialization/callbacks; playkitu does not replace a game's login.
- Microphone Off stops listening immediately, but audio already mixed before that
  click can finish uploading. Redaction does not guarantee arbitrary game logs
  are free of sensitive information; integrators must review their logging.

See [profiles](../packages/com.playkitu.recording/Client/RecordingProfile.cs),
[diagnostic limits](../packages/com.playkitu.recording/Client/RecordingDiagnosticsLimits.cs)
and the [integration guide](unity-integration.md).

## Viewing, costs and release gates

- The library is a **local developer app**, requiring the AWS CLI, an authorized
  retrieval login, .NET tooling and a built native probe. MP4 preview preparation
  also uses the pinned GStreamer developer cache. It is not a hosted team portal.
  Credentials expire and require ordinary sign-in again.
- Recordings play as separate MP4 parts. Originals and missing-data notes remain;
  there is no silently repaired continuous timeline. Log-to-video navigation is
  an estimate when evidence supports it, not guaranteed frame-accurate matching.
  Listing/download bounds are in the [download guide](../tools/download/README.md).
- Cloud video/log retention is 30 days in the supplied configuration. Downloads
  stay on the developer's disk until removed. Uninstalling Unity code neither
  deletes AWS resources nor stops their bills. AWS alerts are not a spending cap.
- Native binaries are built from source. Public binary redistribution preparation
  and clean-machine Windows validation remain release work; an MIT source license
  does not cover every dependency's distribution requirements. See the retained
  [binary release gates](../native/REDISTRIBUTION.md).
- Offline tests and an existing installation do not prove a fresh third-party
  AWS deployment. An older backend/state layout must be migrated deliberately;
  do not apply new Terraform roots assuming extraction preserved its bindings.

When reporting readiness, distinguish source tests, existing-installation tests,
fresh deployment, clean-machine packaging and continuous real capture. Do not
claim all five from one successful recording or decoder result.
