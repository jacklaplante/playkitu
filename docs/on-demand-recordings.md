# On-demand recordings: proposed replacement for shared slots

**Status: design proposal, not implemented or deployed.** The maintained backend
still uses the shared pool described in [capacity](capacity.md). Raising its size
does not implement this design. No cloud change follows from this document.

## Target behavior

Each authenticated, consented recording receives its own private Kinesis Video
Stream. Streams are created when needed and are never reassigned to another
admission. A tester finishing does not impose a cooldown on the next tester.
Late writes remain confined to the original recording. The developer library
continues to list sessions and play/download their video and audio.

There is no built-in four/eight-stream pool or fixed 8/day and 50/month product
quota. Owner-configured admission/concurrency/usage controls remain available;
rate and abuse protection remain mandatory. “No fixed slots” is not a guarantee
of infinite AWS capacity or a billing cap.

## Required changes

1. **Independent session state.** Replace the global Pending/Active/tester maps
   and global heartbeat writes with per-session records and conditional per-tester
   ownership leases. Lease deadlines remain explicit; DynamoDB TTL is cleanup,
   not permission to take a live lease. Keep global recording controls, but do
   not update them on every session heartbeat. Optional exact aggregate quotas
   must have an explicit enforcement/throughput design, not an unbounded map.
2. **Idempotent allocation.** Reserve a durable allocation/cleanup record before
   creating a stream. Derive its immutable name from the server-generated
   admission identity, not a caller-selected stream name or reusable session UUID
   alone. Recover a lost CreateStream response by inspecting that exact stream;
   verify account, region, name, ownership tags, retention and persisted binding.
   Concurrent retries must neither allocate duplicates nor adopt another stream.
3. **Bounded startup.** Create only after Steam/consent checks and reserve capacity
   before creation. Model preparing/ready/failed explicitly; make retries
   idempotent and bounded across Lambda exits and delayed stream activation.
   Preserve gameplay progression and surface recording unavailability. Do not
   silently represent a logs-only result as a successful video recording.
4. **Scoped access.** Keep per-session write-only STS grants restricted to that
   exact stream and immutable media deadline. Separate stream creation/retirement
   permissions from client permissions. Terraform/IAM must bound management to
   this deployment's namespace and required tags. Clients never create/delete
   resources or choose trusted destinations.
5. **Retention and orphan recovery.** Preserve footage for the selected retention
   period. Closing ends allocation/credential refresh, not stored-media access.
   Keep a due-time-indexed durable cleanup record until resource deletion is
   acknowledged; do not expire that record first. Reconcile uncertain creation,
   close/create races, inactive sessions, failed starts and throttled deletion.
   Delete only the verified admission-owned stream after retention and outstanding
   write windows; never delete the old pooled streams during conversion.
6. **Retrieval compatibility.** Resolve a new session's bound ARN from verified
   server-owned metadata within the configured trust namespace. Retain pooled
   slot lookup for old recordings. Update receipts/catalog validation and the
   library without weakening fragment attribution or leaking another session.
   Paginate the session catalogue beyond the current 50-per-day display bound.
7. **Configurable controls.** Replace embedded active/daily/monthly counts with
   validated owner settings and optional disabled count caps. Publish the effective
   settings and distinct rejection reasons. Bound authentication attempts, stream
   creation rate and retries independently of successfully recorded session counts.
   Do not promise refund of real AWS usage when a recording fails.
8. **Terraform migration.** Terraform owns the shared database, service roles,
   cleanup worker/schedule, API and settings. Application-created session streams
   are owned by the application's lifecycle ledger, not added to Terraform state
   one at a time. Migrate the existing handler/state deliberately, retain old
   recordings and make configuration/counter changes explicit. Never reseed CONTROL.

## Capacity and cost proof

The AWS developer guide currently publishes a default 10,000 video-stream quota,
an increase path, and a 50/second CreateStream rate. These are published defaults,
not verified quotas for an owner's account. Retained recordings consume stream
count too: with 30-day retention, 1,000 recordings/day would accumulate roughly
30,000 retained streams even when few testers are online. Check/increase quotas
or adopt an explicitly designed archive path before that workload.

AWS charges for ingested, stored and consumed data; encryption API usage and other
service charges also matter. More sessions increase possible spend. A complete
plan must model session rate, duration, bitrate, retention and viewing, rather
than reuse a small playtest's historical monthly estimate. Establish the actual
account's stream, API, Lambda and database capacity before a live load test.

Sources checked October 24, 2026:
[Kinesis Video Streams quotas](https://docs.aws.amazon.com/kinesisvideostreams/latest/dg/limits.html),
[pricing](https://aws.amazon.com/kinesis/video-streams/pricing/),
[encryption and billing FAQ](https://aws.amazon.com/kinesis/video-streams/faqs/).
[DeleteStream](https://docs.aws.amazon.com/kinesisvideostreams/latest/APIReference/API_DeleteStream.html)
makes its footage inaccessible, so stream retirement must follow retention.

## Proof gates before calling it scalable

- Isolate backend/control-plane tests first; no full game rebuild for allocation
  logic. Exercise simultaneous independent starts, same-session retries, one-tester
  races, unknown commit/create results, deadlines, throttling, cancellation and
  recoverable cleanup. No recorded session may obtain another admission's stream.
- Test more than eight sessions, configurable/disabled quotas, realistic heartbeat
  and authorization rates, and catalogue pagination. Demonstrate bounded per-record
  state and absence of a shared global write on each heartbeat. Fakes establish
  logic, not actual AWS throughput.
- Validate IAM/Terraform offline, review the real migration plan and new management
  permissions, then run a bounded live backend check. Account capacity and measured
  throughput must be reported separately from the software's configured limits.
- Prove existing pooled recordings remain retrievable, a new consented client can
  record/retrieve a dedicated stream, repeated short recordings need no pool
  cooldown, and cleanup preserves footage until its promised expiry.
- Leave platform, best-effort capture and native distribution gates explicit.
  Removing slots does not fix client media loss or establish clean-machine support.
