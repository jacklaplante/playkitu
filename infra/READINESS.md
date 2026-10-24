# Owner readiness and recording controls

This is a fresh-deployment checklist, not a live deployment receipt. Source tests
cannot prove account permissions, Steam entitlement, physical capture, alert
arrival, true cost or remote retention. Keep both switches OFF until a separately
authorized bounded test has current identity, artifact and cleanup evidence.

Use the [deployment sequence](README.md). Confirm the exact app/account/region/API,
reviewed ZIP hash, four streams and roles. Keep publisher key values outside state
and logs. A source build never authorizes cloud deployment or recording.

## Isolated role sessions

Install normal owner process-profile pairs using `access/retrieval-profiles.example.ini`
as a template for deploy, operations and retrieval. Authenticate through the
normal owner MFA/login flow. Set explicit absolute existing `AWS_CONFIG_FILE`,
`AWS_SHARED_CREDENTIALS_FILE` and `AWS_LOGIN_CACHE_DIRECTORY` paths. Never paste
credential output, create static keys or fabricate login-session metadata. Remove
ambient static credentials and endpoint overrides in this dedicated shell.

```powershell
. ./infra/access/operations.ps1
Assert-PlaykituRole -ConfigurationFile ABSOLUTE_OWNER_CONFIG -Profile OWNER_OPERATIONS_PROCESS_PROFILE -Role operations
Get-PlaykituAllowance -ConfigurationFile ABSOLUTE_OWNER_CONFIG -OperationsProfile OWNER_OPERATIONS_PROCESS_PROFILE -DeployProfile OWNER_DEPLOY_PROCESS_PROFILE -PlannedAdmissions 1 -ReviewedIssuerSha256 REVIEWED_NEW_ZIP_SHA256
```

These functions make calls only when explicitly invoked. Allowance verifies the
actual Lambda hash, both boolean switches, zero stored leases and conservative
current UTC quotas without modifying counters or enumerating tester identities.
An available allowance is not activation permission. An expired lease reference
requires the canonical service lifecycle/reclamation path and another verified
read; never delete rows/references manually. Role maximum duration is not proof
of remaining credential lifetime; authenticate normally and plan bounded cleanup.

## Controls

Generate distinct OFF, ON and admitted-session requests with
`access/prepare-control.ps1`. It defaults to OFF/OFF. Use `-AdmissionsEnabled
$true -UploadsEnabled $true` only for an approved test. After its one intended
admission, admissions OFF/uploads ON prevents more starts while preserving that
session's finite upload/finalization path. Global OFF is both false.

```powershell
Set-PlaykituControls -ConfigurationFile ABSOLUTE_OWNER_CONFIG -Profile OWNER_OPERATIONS_PROCESS_PROFILE -Request ABSOLUTE_PREPARED_SWITCH_JSON -ReadRequest ABSOLUTE_PREPARED_READ_JSON -AdmissionsEnabled $false -UploadsEnabled $false
```

The update is followed by strong readback and must return the two actual expected
booleans. Missing, null, strings or a failed read are unverified, never OFF. Prepare
an independently authenticated OFF fallback and finite owned-process deadline
before ON; these control helpers are not a recording supervisor. Do not leave an
unattended activation or automatically retry failed recording runs.

## Evidence and failure reporting

Require immutable run/session/producer identity, consent, actual server deadlines,
normal exit or explicit failure, OFF readback and owned-process absence. Then take
a fresh catalogue with the existing owner download tool. Check metadata counts,
checksums/sequences, stopping manifest, diagnostic drops, persisted result200 ACKs
and actual MKV decode/EOS. For recovery, correlate a bounded real connection event
with post-cut persisted ACK upload identity and newly decoded media, not bytes on
a socket alone. Do not rewrite raw failures or mark incomplete evidence accepted.

Caller/native drops are separate counters. A zero SDK/native audio-drop count
does not erase rejected upstream callbacks. Source timestamp, producer timestamp,
ACK observation and decoded position differ. EOS and sampled clips do not prove
continuous physical capture, lossless recovery or exact log-to-frame timing.
Preserve a navigation tool's unavailable/gap result rather than guessing a frame.

After testing, require strong OFF/OFF, zero references through canonical lifecycle,
owned processes absent, and bounded private evidence. Retention/deletion and
publication remain separate explicit owner actions. Budget alerts are delayed
notifications, not a guaranteed spending cap.
