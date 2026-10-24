# Recording capacity

`videoStreamCount` in the owner-authored `infra/deployment.json` is the single
deployment setting for the shared video pool. It defaults to **4** when omitted;
the supported range is **1–8**. Terraform stream creation, prepared owner
policies and the initial CONTROL lease array use this same value. The runtime
and download tool accept the matching ordered stream ARNs. Unity needs no change.

One stream is leased to one video session. A full or quarantined pool does not
stop gameplay: eligible extra sessions can retain logs with video unavailable.
The separate limits remain eight active sessions, eight admissions per UTC day,
eight per tester per UTC day, and fifty per month. Raising the pool does not raise
those quotas. More than eight simultaneous sessions needs a separate quota and
load review, rather than merely creating more streams.

## Changing an existing installation

Changing the JSON alone does **not** resize a live installation. In particular,
never run the initial `control-seed.json` against an existing CONTROL item.

1. Schedule maintenance, disable admission/upload and allow active recordings
   and finalization to finish. Existing credentials can outlive a session.
2. Change `videoStreamCount`, review the Terraform plan and any exact-ARN owner
   permissions, then create the additional streams. Keep existing slot numbers
   and streams; their recordings remain subject to retention.
3. Update the backend's ordered stream ARNs and the owner's download JSON.
4. With an empty, safely retired pool, conditionally update the existing CONTROL
   slot array using its current version. Preserve counters, flags and retained
   quarantine information. Never resize by overwriting an active lease.
5. Verify that configured ARNs and CONTROL slot count agree, test, then restore
   availability. A mismatch returns `slot_configuration_mismatch`, without
   silently reassigning another recording's stream.

Released streams remain quarantined until fifty minutes after their last issued
media credential expires. This intentionally delays reuse to protect sessions
from late writes by a previous client. It can make the available pool smaller
than the number of currently playing testers.

The deployment setting makes the intended size easy to change and keeps the
generated components consistent; it is not an automatic production migration.
Increasing capacity also increases possible usage/cost. Existing deployments
with older backend handlers must migrate those bindings deliberately.
