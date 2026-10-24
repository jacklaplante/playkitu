# Recording capacity and limits

## On-demand streams

Use `infra/deployment.on-demand.example.json` for a **new installation**. Each
admission receives a uniquely named private stream; it is never assigned to
another recording. There is no fixed video pool and no slot-reuse cooldown.
The backend records allocation/cleanup work before attempting creation, recovers
uncertain responses by the same admission name, and verifies ownership tags.

```json
{
  "streamMode": "onDemand",
  "videoStreamCount": 0,
  "recordingLimits": {
    "concurrentSessions": null,
    "admissionsPerDay": null,
    "admissionsPerMonth": null,
    "testerAdmissionsPerDay": null,
    "admissionAttemptsPerMinute": 120,
    "sessionRequestsPerMinute": 30
  },
  "lambdaConcurrency": null,
  "apiRequestsPerSecond": 100,
  "apiBurst": 200
}
```

This excerpt is combined with the example's owner trust fields. **Null disables
a count cap**; positive integers enable it. Zero is rejected. Daily/monthly
periods are UTC. Counts remain tracked while caps are disabled, and changing a
setting does not refund or reset prior admissions. Reservations count after
Steam verification, before stream preparation; failed preparation can consume an
admission. One active/preparing session per tester remains an ownership rule.

Settings are deployment configuration: change them, review the Terraform plan and
deploy. They are not hardcoded product quotas or a tester-controlled API. Enabling
or lowering a cap does not terminate existing sessions; it restricts new admissions.
After a crash, an optional concurrency cap is released by scheduled lease cleanup;
backlog/failure can delay that release. Explicit Close releases it immediately.

Rate protection stays enabled. The example permits 120 admission attempts/minute
and 30 authorization requests/session/minute. API rate/burst and Lambda capacity
are separate. Null Lambda concurrency uses the account's unreserved capacity,
not unlimited AWS capacity. Exact counters contend at admission/retirement;
session heartbeats update only the session, its tester lease and lifecycle work.
Actual throughput requires AWS quota checks and a bounded live load test.

## Retention and viewing

Streams retain media for 30 days. Closing stops new grants but preserves the
stream for viewing; expired credentials cannot affect the next recording because
it has a different stream. A durable ledger schedules retirement after the media
deadline plus retention and a write-window margin. Cleanup has no TTL until a
verified, version-fenced delete is acknowledged. The minute worker handles bounded
pages from sixteen work shards, with four concurrent reconciliations and a
55-second budget. Failures defer retry and surface as Lambda errors. Monitor
backlog/throughput before increasing admission rates.

The library follows verified per-session ARNs and retains pooled lookup for old
recordings. Its Load more control pages beyond fifty sessions/day; each request
still bounds work and retains the 31-day date-range limit. Downloaded originals
and media-attribution checks are unchanged.

More retained sessions consume more AWS streams as well as storage. The published
default [stream quota](https://docs.aws.amazon.com/kinesisvideostreams/latest/dg/limits.html)
is 10,000, with an increase path; this is not a verified
account quota. With thirty-day retention, 1,000 sessions/day would retain roughly
30,000 streams. Check account stream/API/Lambda/database quotas and estimate
ingestion, storage, viewing and encryption usage. Count caps and alerts are not
hard spending caps. See the [on-demand design and proof gates](on-demand-recordings.md).

## Existing pooled installations

Omitted `streamMode` means `pooled`, preserving the existing deployment contract.
That mode retains the old 1–8 shared streams, eight active sessions, eight
admissions/day and fifty/month. Released streams remain quarantined until fifty
minutes after their last issued media credentials expire. Extra admissions can
be logs-only. `recordingLimits` is supported only in on-demand configuration.

In on-demand mode, `videoStreamCount` is **only the number of old Terraform-owned
pooled streams retained for compatibility**; it is not a concurrency limit.
Fresh installations use zero. Migrating an installation with four streams must
preserve four and its exact ordered ARNs. Do not remove them from state/resources
or delete retained recordings to adopt the new mode.

## Migration gate: do not reseed CONTROL

The on-demand backend requires CONTROL schema 2 with empty global slot/pending/
active maps. A mismatch fails closed. The conditional seed helper is for an empty
installation only; it cannot migrate an existing CONTROL item.

1. Review the existing Terraform state/backend, handler, identities, exact resource
   bindings and both role policies/permissions boundary. An extracted source tree
   is not evidence that a live state migration is safe.
2. Disable new admissions, allow old sessions/finalization to drain, then disable
   uploads. Verify pending/active admissions are gone; retain old streams and data.
3. Prepare/review new stream namespace/tag permissions, CONTROL transaction-fence
   permission, the due-work index, minute cleanup event, runtime settings and ZIP.
   No client gets creation or deletion authority. Review costs and actual quotas.
4. Prepare a conditional state migration from the strongly read old version/data
   and verified OFF/OFF flags. Preserve old daily/monthly/tester totals in the
   corresponding `USAGE#DAY#date`, `USAGE#MONTH#month`, `USAGE#TESTER#hash#date`
   scalar counter records (`UsageCounter`), and initialize `USAGE#ACTIVE` to zero.
   Preserve period labels; never silently reset usage. Write schema 2 and counters
   atomically, conditioned on absent new counter rows and the exact old CONTROL.
   Keep a private migration receipt/backup; recover unknown results by readback.
5. Apply only the reviewed migration/runtime plan, preserving existing pool
   resources, data, identity encoding and public API bindings. Update owner
   retrieval metadata to on-demand mode with the old ordered stream ARNs retained.
6. While OFF, verify schema, policy scope, cleanup, old-recording retrieval and
   effective configuration. Then run an authorized bounded creation/recording/
   retrieval/retirement check before reopening admissions. Do not treat offline
   tests as actual AWS throughput, deployment or capture evidence.

No existing AWS installation was migrated or resized by implementing this source.
