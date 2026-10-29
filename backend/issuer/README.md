# Playkitu authorization service

.NET 10 / AWS Lambda service for Steam-authenticated Windows Unity recordings.
This source has been tested offline; shipping this directory does not deploy or
activate anything. See [infrastructure deployment](../../infra/README.md).

## Build, test, package

From the repository root in PowerShell 7:

```powershell
pwsh -NoProfile -File backend/issuer/prepare.ps1
pwsh -NoProfile -File backend/issuer/test.ps1
pwsh -NoProfile -File backend/issuer/package.ps1
```

Preparation obtains the SHA512-pinned .NET SDK 10.0.402 and restores locked NuGet
packages. It uses ignored `private/` directories, disables development certificate
creation/telemetry/metadata credential discovery, and never installs a global SDK.
An existing exact SDK may be selected with `PLAYKITU_DOTNET_ROOT`.
The normalized Linux x64 managed ZIP is `private/issuer.zip`; its receipt is
`private/evidence/artifact.json`. Review that newly built hash before deployment.
No historical deployment hash is an accepted default.

The Lambda handler is `Playkitu.Issuer::Playkitu.Issuer.Function::HandleAsync`.
Terraform sets `PLAYKITU_DEPLOYMENT` to nonsecret owner trust/resource references.
The service validates it before constructing any AWS client. The publisher key
value is read only from the configured Secrets Manager secret. Provision its
restricted Steam publisher key privately outside Terraform; never pass it through
Terraform variables, state, command arguments, manifests, source or logs.

## Trust and protocol

The optional build.playerDisplayName is private host-declared recording metadata,
bounded to 80 nonblank characters with no control characters. It is stored with
the session and included in the immutable admission binding, never used for Steam
authorization, quota identity or role selection. Null names are omitted from
serialization so pre-feature requests keep their exact binding hashes.
Config advertises playerDisplayNames=true. Updated clients include the name in
admission only when that capability is present; older issuers keep accepting their
legacy requests. Deploy the issuer and reader before shipping a named client.

Owner settings bind the Steam app, AWS account/region, exact issuer origin, the
ordered stream ARNs, bucket/table, role path, media/retrieval roles and secret ARN.
These values cannot come from a player request or downloaded recording. Defaults
for identity encoding are `pkrec2:` and `playkitu-recording:`. An integrator may
explicitly configure a different identity prefix/domain on BOTH client and issuer;
there is no automatic downgrade or fallback. The portable package supplies the
single canonical identity/proof and diagnostic-limit sources.

Protocol v1 routes remain `GET /v1/config`, `POST /v1/sessions`, and session
`authorize` / `close` routes. JSON fields, including `build.gameVersion`, retain
their wire spelling. Steam verification checks the bound per-admission identity
using Valve's `str:` representation and requires an active app ownership verdict.
Consent validation precedes authentication/state mutation. Replay fencing,
canonical 256-bit bearer proofs, immutable bindings and atomic reservations remain.

Pooled compatibility limits: a configured pool of 1–8 precreated streams (four by default), eight active/pending admissions, one session per
tester; eight successful admissions/tester/UTC day, eight globally/day and fifty
monthly, without refunds. Sessions have a 14,400-second media deadline and
180-second inactivity timeout. Optional negotiated diagnostic finalization is
exactly 60 seconds; it never extends media credentials. Legacy absent/zero opt-in
retains the original binding encoding and 64 MiB/4096-object diagnostic budget;
opt-in permits 256 MiB/8192 objects with 4096 identities per kind. Client attempted
bytes remain 512 MiB. All lifetimes, per-object/request caps, quarantine and
publication-after-deadline guards stay finite and tested.

These admission quotas are backend code limits, independent of `videoStreamCount`.
Closing does not refund an admission. Full/quarantined video pools can admit
logs-only sessions; slot reuse waits until 50 minutes after the last credential's
expiry. See [current limitations](../../docs/limitations.md) and
[safe capacity changes](../../docs/capacity.md).

With explicit `streamMode: "onDemand"`, CONTROL schema 2 and the matching IAM/
cleanup infrastructure, the service instead allocates a unique stream for each
verified admission. `recordingLimits` configures concurrent/day/month/tester count
caps (null disables them), admission attempts/minute and per-session requests/minute.
Count reservations are durable before creation; retries recover the same admission.
Verified pending admissions resume after contention without a new Steam request.
Heartbeats use session/tester/work records and a CONTROL condition check, not a
global active-session map write. Exact usage counters serialize admissions and
retirement. The internal scheduled event `version=playkitu.cleanup.v1` reconciles
lease expiry and retained stream retirement; errors fail the Lambda invocation.
Read [configuration, retention and migration](../../docs/capacity.md) before use.

`seed.ps1` emits the canonical model's initial CONTROL item OFF/OFF, without
constructing AWS clients. The infrastructure helper wraps it in a conditional
insert. Missing CONTROL fails closed. Never replace an existing CONTROL item or
reset counters to run another test.

## Session-end email (optional)

On-demand installations can enable email through the existing SNS alert topic.
The client and game build do not change. Runtime `session_notifications_enabled`
sets `PLAYKITU_NOTIFICATION_TOPIC_ARN`; the handler validates this against the
exact deployment topic. Admission freezes notification eligibility, so existing
sessions and historical recordings never generate a backlog of alerts.

Authenticated close persists the client reason/time and a notification in the
same transaction as capacity release. Inactivity cleanup labels abandoned
stream-ready sessions `interrupted`; failed stream preparation sends no alert.
The existing minute worker claims due work before publishing and retries failures
without blocking gameplay. Acknowledged sends leave the due queue until ordinary
retirement. Pending sends expire with stream retention. SNS standard delivery or
an uncertain publish/ack can cause a rare duplicate; the session ID identifies it.

Alerts contain bounded player/build labels, approximate observed activity length,
UTC times, session ID and ending classification. They contain no footage, logs,
proofs, credentials or public download links. A client close is not proof all
uploads arrived. Timeout detection follows the three-minute inactivity threshold,
then scheduled processing, so interrupted alerts can take several minutes.

Activation requires owner-reviewed exact-topic `sns:Publish` in both the issuer
policy and its external boundary, plus a confirmed existing email subscription.
Rebuild/update the developer download library alongside the issuer: its strict
stored-state reader must recognize the new session ending/notification fields.
See [notification maintenance](../../infra/README.md#session-end-email-maintenance).

## Tests and limits

Tests use explicit fake credentials and blocked/fake HTTP transports; they do not
request Steam tickets, publisher secrets or real AWS credentials. Neutral default
and explicitly selected fake compatibility identities have independent golden
vectors. Four-hour fixture tests are optional and require an explicitly supplied
neutral production export; an absent fixture is reported as skipped.

Authorization/signing is not evidence of persistence. Old recordings remain
private and subject to retention. Previously issued narrow S3 grants remain usable
until their short expiry even after close; no instantaneous revocation is claimed.
