# Current limits and release status

playkitu is experimental and source-first. These limits affect whether a tester
gets useful footage; they are not just implementation details. Defaults below
describe the maintained source, not the state of a particular AWS installation.

## Capacity and admission

| Limit | Current behavior | Changing it |
| --- | --- | --- |
| Stream allocation | Explicit `onDemand` mode creates a private stream for each admission, with no pool or reuse cooldown. Creation may take several polls; failure cannot guarantee footage. | Use the on-demand deployment example. Existing installations need the migration below. |
| Count caps | On-demand concurrent, daily, monthly and per-tester daily caps are deployment JSON fields. `null` disables an application count cap; positive integers enforce one. Reservations count even if capture subsequently fails. | `recordingLimits`; one active/preparing session per tester remains enforced. |
| Service capacity | AWS stream/API/database/Lambda quotas, admission-counter contention and request throttles still bound throughput. Retained recordings consume stream quota until cleanup deletes their streams. | Review actual account quotas, rates, costs and load; disabling count caps is not infinite capacity. |
| Legacy pooled mode | Omitting `streamMode` preserves the old four-stream default, 1–8 configurable streams, roughly 65-minute reuse delay, eight active sessions, eight admissions/day and fifty/month. Extra admissions can be logs-only. | A coordinated migration to on-demand removes those pool constraints; a source update alone does not change live AWS. |

The on-demand implementation and failure cases have offline regression coverage;
live stream creation, cleanup and throughput are separate deployment proof gates.
See the [capacity/migration guide](capacity.md) for defaults, failure behavior and
the required state/permissions migration. Never reseed existing CONTROL or delete
old pooled streams to switch modes.

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

- Optional session-end email is available for on-demand installations after
  owner-reviewed SNS access, runtime enablement and subscription confirmation.
  It is off by default and never backfills already-admitted sessions. A normal
  ending does not verify complete footage/log delivery. Interrupted sessions are
  detected after inactivity and scheduled checks, rather than immediately.
  Durable retries stop at stream retirement; SNS/publish acknowledgement failures
  can produce rare duplicates. Live delivery must be proved separately from tests.

- The library is a **local developer app**, requiring the AWS CLI, an authorized
  retrieval login, .NET tooling and a built native probe. MP4 preview preparation
  also uses the pinned GStreamer developer cache. It is not a hosted team portal.
  Credentials expire and require ordinary sign-in again.
- Player names require a host-provided name, updated client and an issuer advertising
  playerDisplayNames. Existing anonymous recordings cannot recover old usernames;
  local operator labels remain available. Names are display metadata, not identity
  verification. Displayed session length is approximate
  observed activity, not a promise of continuous or complete playable footage.
  Log downloads preserve uploaded chunks and any missing-data notes.
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
