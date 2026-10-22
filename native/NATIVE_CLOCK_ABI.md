# Opt-in synthetic clock evidence (ABI 2)

The runner still reports ABI **1** and copies **264-byte** `SbrecObservation` records. Existing settings/status/layouts and exports are unchanged. The additive clock capability is version **2**, with **256-byte**, alignment-8 records. This measures generated video scheduling time, not physical capture, arrival, upload, persistence or continuous coverage. No managed integration is included.

## Calling contract

Use Cdecl/noexcept exports from `PlaytestRecordingNativeProof.dll`:

```cpp
uint32_t sbrec_runner_clock_v2_info(uint32_t* version, uint32_t* recordBytes);
uint32_t sbrec_runner_enable_clock_v2(uint64_t handle, uint32_t version,
    uint32_t recordBytes, uint32_t capacity, uint32_t intervalMs);
uint32_t sbrec_runner_drain_clock_v2(uint64_t handle, FrameAnchorV2* output,
    uint32_t recordBytes, uint32_t capacity, uint32_t* written, uint64_t* droppedTotal);
```

Check capability before enabling; missing exports mean unavailable. Enable between create/start, while CREATED and not retired. Require version/size 2/256, capacity 1–64, interval 1000–10000 ms. Identical configuration is idempotent; changes or enable after start return BUSY. Allocation/collection is opt-in. Drain has an independent queue and maximum batch 64. Wrong size is rejected before copying and sets written=0; disabled returns NOT_READY. Return codes remain OK 0, INVALID 1, BUSY 2, INTERNAL 3, NOT_READY 4. Caller owns valid buffers/pointers, as for ABI 1.

Exact field order is in [native-synthetic-runner.h](native-synthetic-runner.h): eight uint32 fields at offsets 0..28, then 28 uint64 fields at 32..248. `qpcBefore` starts 64; `sdkPts100ns` at248. Use unsigned 64 integers, never floating point. Unknown optional uint64 values are UINT64_MAX (serialize as null); zero is valid. Segment sequence 0 is unavailable. Sequence/clockEpoch start 1; producerEpoch starts 0. Immutable owning settings supply session/run identity. No pointer/clock address is serialized.

Kinds: FRAME 1, BOUNDARY 2, SUPPORT 3. Flags: SOURCE 1, ENCODED 2, SUBMITTED 4, ESTIMATE 8, KEY 16. A usable frame requires reason 0 and all first four flags. SUPPORT carries raw clock evidence, not independent frame/SDK evidence. Boundary carries an invalidation reason; unused fields are unknown.

Reasons: VALID 0, MISSING 1 (reserved), CHANGED 2, SEGMENT 3, UNKNOWN 4, ARITHMETIC 5, AMBIGUOUS 6, OBSERVATION_LOSS 7, PENDING_LIMIT 8, UNMATCHED_END 9, UNSUPPORTED 10, SEGMENT_CHANGED 11, PRODUCER_RESET 12, NO_BRACKET 13, OUTPUT_LOSS 14. Missing/unsupported clock currently uses UNSUPPORTED. Unavailable frames clear ESTIMATE and set estimate/uncertainty unknown; raw stages can remain for diagnosis.

## Measured enclosure and matching

The selected source sample retains QPC before/after `gst_clock_get_time`, QPC frequency, current Gst clock time, effective pipeline/source base time, resolution and calibration tuple. Source and parent pipeline must share clock/base. Only monotonic GstSystemClock without a master is supported. Unstable clock/base/calibration splits clockEpoch and clears history. No shared origin or universal affine correction is assumed.

The existing 10 ms monitor retains at most 256 samples. For checked target `T = pipelineBaseTimeNs + sourceRunningNs`, two same-epoch samples must strictly satisfy `C_lower < T < C_upper`. No extrapolation. Monotonicity encloses scheduling QPC in `[lower.qpcBefore, upper.qpcAfter]`. Estimate is the integer midpoint; uncertainty is the upward-rounded half-width converted to nanoseconds plus clock resolution. Actual samples emit as two SUPPORT records before FRAME, sharing frameOrdinal/clockEpoch/producerEpoch. The FRAME raw tuple is the source-pad sample, not a replacement for the pair. **Retain all three records.** This third kind refines planning so consumers can audit the enclosure.

Current full sticky segments/sequence IDs are inspected on every source/encoded buffer; `gst_segment_to_running_time_full` computes running time. Serialized start/base alone are not a complete transform. Only safe TIME,1x segments are supported. Source→encoded→successful video SDK submission match exact normalized running time within producer epoch. Duplicates mark ambiguity. Finalization waits for all three watermarks to advance or callback quiescence; unknown watermarks do not advance matches. Regression, segment/clock/calibration changes, SDK reset, unknown timestamps and ABI 1 observation loss invalidate affected pending work. Never join across epochs.

Fixed storage: 16 pending matches, 64 output records maximum, 256 clock samples. Select first eligible video frame, then at most one per interval; restart after boundaries. Clock sampling uses selected source callbacks and monitor; encoded/SDK stages do no clock synchronization. Reserve two supports plus frame atomically. Insufficient space emits an unavailable OUTPUT_LOSS frame if possible and increments droppedTotal/sequence gaps. Consumer loss must invalidate incomplete groups. Drain frequently; capacity<3 cannot yield valid groups. Opening targets without preceding samples and delays beyond history legitimately become unavailable.

## Decoder and consumer handoff

Existing decoder schema 1 is preserved. Optional CLI `--select-video-pts-ns CSV --tolerance-ns N`; wrapper `-SelectedVideoPtsNs`/`-ToleranceNs`. Maximum 64 distinct exact nonnegative decimal targets <=INT64_MAX, tolerance 0..1000000 ns. Reject invalid arguments before output reservation. Optional `frameSelections` has schema 1, scope `observed_file`, per-query queryId/targetPtsNs/toleranceNs/matchCount/status plus segmentIndex/bufferOrdinal/ptsNs/runningNs/streamNs/durationNs. Uint64 values stay decimal strings/null. `unique_observed` requires successful decode/EOS, exactly one observed video match and usable times. Otherwise missing, ambiguous or incomplete with selected fields unavailable. GAP/corrupted buffers do not match. Check exit status and whole-result bounds/errors.

A later managed writer must preserve raw groups, run/session, native caller correspondence and uncertainty/loss/epoch fields. Convert QPC to session elapsed with checked integers using its measured managed Stopwatch bracket/frequency. **B2h uses a native QPC caller, not a managed Stopwatch proof.** Join SDK `sdkPts100ns*100` to one unique decoded query within explicit Matroska tolerance, retaining file SHA256, stream/segment/ordinal. Do not use counts, first/last summaries, ACK arrival, a fixed 753400 ns residual or encoder raw 1000-hour PTS as correspondence. Do not interpolate sparse points without a separately verified policy. Combine clock/managed bracket uncertainty and decoder quantization tolerance in any displayed bound.

Generic producerAnchor/playbackOffsetVerified, source opening/tail and continuous-coverage flags stay false. File-local selected evidence does not establish completeness, fragment identity or arbitrary retrieval correspondence. Missing opening/tail queries stay missing, with no nearest-frame fallback.

## Offline proof

Run `build-adapter.ps1`, then `test-clock.ps1`. Generated patterns/tone, existing intercepted SDK, isolated registries/minimal environment, bounded processes and fake memory fixtures only. The suite refuses plugins lacking the interception export. Normal/overflow load the packaged DLL. Delay compiles identical runner source with a test-only 250 ms source callback delay; production DLL has no delay hook/export. Source PTS/running/base stay identical while pad arrival lies beyond scheduling enclosure. Units cover resets, duplicates, limits, unknowns and unmatched tail; real selected later frames pass through SDK-produced Matroska and maintained decode.

Literal bundles: `tests/fixtures/clock-{normal,delay}-{native,decoded,correspondence}-v2.json` and overflow native result. Decoder schema remains 1; filename v2 identifies the bundle. The [fixture guide](../tests/fixtures/README.md) and its provenance manifest retain hashes and limits. Historical ABI 1 IL2CPP results are not ABI 2 proof.
