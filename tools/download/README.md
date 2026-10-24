# Playkitu owner download tool

Read-only AWS catalogue/media retrieval plus offline inspection and log navigation.

## Playtest library — list, select, watch

The local browser app lists individual sessions by admission date, lets you select
a playtest, and downloads its video/audio and diagnostic evidence. MP4 previews
play in the browser, with an original MKV download and an Open download folder
button. Downloaded sessions remain available after restarting the app. Microphone
audio, when recorded, is part of the same audio track.

After preparing the tool and owner configuration below, run:

```powershell
pwsh -NoProfile -File tools/download/browse.ps1 -Deployment C:/absolute/owner-download.json -Profile OWNER_PROCESS_PROFILE
```

The app opens in your browser. Select dates (up to 31 days), choose a session,
then **Download recording**. It reads metadata pages and selects media windows
automatically; no producer timestamps or AWS console stream selection are needed.
Only one download runs at a time, with progress and cancellation. The default
destination is `tools/download/private/library`; `-Output ABSOLUTE_DIRECTORY`
changes it. `-NoOpen` prints the URL without opening a browser.

Playback uses the native runtime plus the pinned GStreamer developer cache from
`native/prepare.ps1`. The small game runtime intentionally omits MP4 muxing; the
app does not modify it. `-NativeRuntime ABSOLUTE_DIRECTORY` selects the native
probe/runtime. If preview conversion fails, the original MKV is still available
for a player such as VLC. No FFmpeg installation is required.

The server listens only on 127.0.0.1 with a fresh private URL, rejects cross-site
requests and never sends AWS credentials to the page. Close its terminal to stop
it. AWS login expiry requires ordinary sign-in and restarting the app, not new
permissions. Browser access never changes recording controls or cloud resources.

Sessions are read from the retained day index; expired/stale index entries are
skipped. Each request returns at most fifty sessions; **Load more playtests**
continues through the selected days with an opaque continuation cursor. There is
no fifty-per-day display ceiling. Changing the date range starts a new listing.
Live sessions
are partial observations. Video is kept in separately playable parts, preserving
missing-footage warnings rather than silently stitching gaps. A whole download
is bounded to two hours, 16 GiB of media-window transfers, and 64 ten-minute
producer windows; metadata and individual windows retain the existing bounds.

## Low-level retrieval and evidence

It preserves original compressed objects, hashes, receipts, timing claims, MKVs,
probe results and missing-data status. It does not remux, repair gaps, infer
physical capture accuracy or call incomplete recordings complete.

## Prepare and validate

From the repository root with PowerShell 7:

```powershell
pwsh -NoProfile -File tools/download/prepare.ps1
pwsh -NoProfile -File tools/download/test.ps1
```

The test launcher supplies empty isolated AWS configuration and fake process
profiles; local CLI inspection is exercised without cloud credentials. Tests link
the canonical `tests/fixtures` data, never copied game/runtime evidence. Optional
native checks need a locally generated media file and the source-built native
runtime; they are reported separately from fake-probe unit tests.

Build the native probe using [native instructions](../../native/README.md). Default
runtime is `native/private/runtime`, containing `bin/native-media-probe.exe`.
An explicit `--native-runtime ABSOLUTE_DIRECTORY` is passed to every decoder call.
A missing/invalid explicit directory errors; it never silently selects a default.
The decoder subprocess receives no AWS configuration or credentials.

## Owner configuration

After the separately approved infrastructure deployment, save the NONSECRET output
using `terraform -chdir=infra/runtime output -json download_configuration` into an
ignored private JSON file. Review it against the owner's chosen account, region,
app, exact API origin, role, bucket, table and configured streams. Never derive trust from
a session/manifest/API response. `deployment.example.json` contains fake values
only and is never automatically loaded.

Cloud commands require `--deployment` with that absolute JSON path, an explicit
named `credential_process` profile, and explicit absolute existing
`AWS_CONFIG_FILE`, `AWS_SHARED_CREDENTIALS_FILE`, `AWS_LOGIN_CACHE_DIRECTORY` paths.
Use the normal AWS login flow for the owner's dedicated retrieval role, then its
process profile. Do not invent login-session metadata, paste/export credential
JSON, use static keys or pass credentials on the command line. STS must return the
exact configured account and retrieval role. Ambient endpoint overrides are
ignored; discovered KVS endpoints must be HTTPS in the configured AWS region.

```powershell
pwsh -NoProfile -File tools/download/download.ps1 list --day 2026-01-01 --profile OWNER_PROCESS_PROFILE --deployment ABSOLUTE_DEPLOYMENT_JSON
pwsh -NoProfile -File tools/download/download.ps1 metadata --session SESSION_UUID --profile OWNER_PROCESS_PROFILE --deployment ABSOLUTE_DEPLOYMENT_JSON --output NEW_ABSOLUTE_PAGE_DIRECTORY
# Continue with --previous-page PREVIOUS_ABSOLUTE_PAGE_DIRECTORY until the listing ends.
pwsh -NoProfile -File tools/download/download.ps1 fetch --session SESSION_UUID --profile OWNER_PROCESS_PROFILE --deployment ABSOLUTE_DEPLOYMENT_JSON --metadata-page LAST_ABSOLUTE_PAGE_DIRECTORY --producer-from-utc ACTUAL_PRODUCER_START_UTC --producer-to-utc ACTUAL_PRODUCER_END_UTC --output NEW_ABSOLUTE_WINDOW_DIRECTORY
```

Select actual persisted result-200 ACK producer fragment-start timestamps. Windows
are half-open fragment START intervals, so a returned final fragment can extend
past the interval. Listing an active session is a non-atomic partial observation;
concurrent insertions before the cursor may be absent. A final catalogue must be
fresh after shutdown. Do not reuse active snapshots as final retention evidence.

Bounds remain 256 objects/300 seconds per metadata page, at most 32 pages/8192
objects and 8 MiB catalogue indexes. Windowed retrieval is at most 600 producer
seconds, 1024 fragments, 1 GiB downloaded bytes, 64 MiB expanded metadata, and
20-second media batches. These are maxima, not instructions to download everything;
short explicit windows are normally sufficient. No automatic failure retry exists.

## Offline commands

These require no profile or deployment file:

```powershell
pwsh -NoProfile -File tools/download/download.ps1 inspect --directory SAVED_ABSOLUTE_BUNDLE
pwsh -NoProfile -File tools/download/download.ps1 map --directory SAVED_ABSOLUTE_BUNDLE --elapsed-ns EXACT_INTEGER
pwsh -NoProfile -File tools/download/download.ps1 locate-log --directory SAVED_ABSOLUTE_BUNDLE --log-file logs-0000.log --line ACTUAL_HEADER_LINE --output NEW_ABSOLUTE_QUERY_DIRECTORY
```

`locate-log` selects a real hash-verified callback header by one-based line or
zero-based UTF8 byte offset. It uses existing clock evidence and actual decoded
endpoints, with a bounded selected-frame probe. Success is only `estimated`, not
verified physical capture or original log-emission timing. Gaps, resets, missing
relations and ambiguous evidence return `unavailable`. No nearest-frame fallback.
Strict `map` remains a separate constructed/generated scheduling proof.

Exit status: 0 for successful retrieval/local operation, 3 for incomplete or
unavailable evidence, 2 for invalid input/read failure, 124 for bounded cancellation.
Always retain the original status and issue list. Decode EOS alone does not prove
whole-session continuity, source opening/tail completeness or lossless recovery.
