#pragma once
#include <cstdint>
#include "native-media-config.h"
#ifndef SBREC_RUNNER_EXPORT
#define SBREC_RUNNER_EXPORT __declspec(dllexport)
#endif
#define SBREC_CREDENTIALS_ABI_ONLY
#include "native-session-credentials.h"
#undef SBREC_CREDENTIALS_ABI_ONLY

// Versioned in-process C ABI. UTF-8 identity fields are fixed, NUL terminated;
// no secrets, paths, borrowed pointers or arbitrary pipeline in observations.
enum : uint32_t { RUN_OK=0, RUN_INVALID=1, RUN_BUSY=2, RUN_INTERNAL=3, RUN_NOT_READY=4 };
enum : uint32_t { RUN_CREATED, RUN_INITIALIZING, RUN_SENDING, RUN_STOPPING, RUN_TERMINAL };
enum : uint32_t { RUN_NONE, RUN_COMPLETE, RUN_CANCELLED, RUN_INIT_FAILED, RUN_START_TIMEOUT,
    RUN_MEDIA_FAILED, RUN_BYTE_LIMIT, RUN_FLUSH_TIMEOUT, RUN_CREDENTIAL_FAILED, RUN_RETIRE_FAILED };
enum : uint32_t { OBS_SOURCE=10, OBS_ENCODED=11, OBS_STATE=12, OBS_STARTUP=13 };
// Additive finite startup observations in the existing ABI1 ring. No layout or
// state enum changes; older consumers may ignore kind13. result is elapsed ms.
enum : uint32_t { STARTUP_WORKER=1, STARTUP_GST_READY, STARTUP_PIPELINE_READY,
    STARTUP_PLAYING_ENTER, STARTUP_PLAYING_RETURN, STARTUP_INPUT_READY, STARTUP_FIRST_SDK_FRAME };
// For submitted SDK observations only, uncertainty100ns also includes the <1ms
// UTC translation-grid bound; ordinary QPC/UTC and DSP measurement brackets stay unchanged.
struct SbrecClock { uint64_t qpc, frequency, unix100ns, uncertainty100ns; };
struct SbrecRunSettings {
    uint32_t size, seconds, width, height, fps, observationCapacity;
    uint32_t startupMs, flushMs;
    uint64_t encodedByteLimit;
    char sessionId[37], runId[37], streamName[129];
    // Public trust file; passed privately to SDK device info, never via env.
    char caFile[1024];
    // Caller brackets sbrec_runner_clock using its own monotonic clock. QPC is
    // sampled by that API; uncertainty includes both caller and native brackets.
    uint64_t managedTicks, managedFrequency, correspondenceQpc, correspondenceUncertainty100ns;
};
struct SbrecObservation {
    uint64_t sequence;
    uint32_t kind, track, flags, result;
    SbrecClock clock;
    uint64_t ptsNs, segmentStartNs, segmentBaseNs, runningNs, sdkPts100ns;
    uint64_t upload, timecodeMs, bytes, epoch;
    char fragment[129];
};
struct SbrecRunStatus {
    uint32_t size, state, reason, degraded;
    uint64_t observations, droppedObservations, encodedBytes, acceptedVideo, acceptedAudio;
    uint64_t receivedAcks, persistedAcks, resets, gaps, credentialRevision, credentialResult;
    SbrecClock firstSource, lastSource;
    uint64_t sourceFrames, firstVideoRunningNs, firstVideoSdk100ns, lastVideoRunningNs, lastVideoSdk100ns;
    uint64_t firstAudioRunningNs, firstAudioSdk100ns, lastAudioRunningNs, lastAudioSdk100ns;
    uint32_t workerDone, callbacksQuiescent;
};

// Additive ABI2. ABI1/version/layout/drain are unchanged. Opt-in video anchors
// describe generated scheduling time, never physical capture or durability.
enum : uint32_t { CLOCK_FRAME=1, CLOCK_BOUNDARY=2, CLOCK_SUPPORT=3 };
enum : uint32_t { CLOCK_SOURCE=1, CLOCK_ENCODED=2, CLOCK_SUBMITTED=4, CLOCK_ESTIMATE=8, CLOCK_KEY=16 };
enum : uint32_t { CLOCK_VALID=0, CLOCK_MISSING=1, CLOCK_CHANGED=2, CLOCK_SEGMENT=3,
    CLOCK_UNKNOWN=4, CLOCK_ARITHMETIC=5, CLOCK_AMBIGUOUS=6, CLOCK_OBSERVATION_LOSS=7,
    CLOCK_PENDING_LIMIT=8, CLOCK_UNMATCHED_END=9, CLOCK_UNSUPPORTED=10,
    CLOCK_SEGMENT_CHANGED=11, CLOCK_PRODUCER_RESET=12, CLOCK_NO_BRACKET=13, CLOCK_OUTPUT_LOSS=14 };
#pragma pack(push,8)
struct FrameAnchorV2 {
    uint32_t size,version,kind,flags,track,reason,sourceSegmentSeq,encodedSegmentSeq;
    uint64_t sequence,frameOrdinal,clockEpoch,producerEpoch;
    uint64_t qpcBefore,qpcAfter,qpcFrequency;
    uint64_t gstClockTimeNs,pipelineBaseTimeNs,clockResolutionNs;
    uint64_t calibrationInternalNs,calibrationExternalNs,calibrationRateNumerator,calibrationRateDenominator;
    uint64_t padArrivalQpc,estimatedScheduleQpc,scheduleUncertaintyNs;
    uint64_t sourcePtsNs,sourceDurationNs,sourceRunningNs,sourceSegmentStartNs,sourceSegmentBaseNs;
    uint64_t encodedPtsNs,encodedRunningNs,encodedSegmentStartNs,encodedSegmentBaseNs;
    uint64_t sdkInputRunningNs,sdkPts100ns;
};
#pragma pack(pop)
static_assert(sizeof(SbrecObservation)==264,"ABI1 observation must not change");
static_assert(sizeof(FrameAnchorV2)==256 && alignof(FrameAnchorV2)==8,"ABI2 layout");

// Additive external-input capability V1. Existing runner/clock ABIs are unchanged.
// Both tracks timestamp the first pixel/sample with the SAME native QPC clock.
// originQpc/frequency come from external_status after ready=1; PTS is the exact
// scaled captureQpc-originQpc interval. Caller timestamps are provenance supplied
// by the caller, not an attestation of physical capture or generated scheduling.
enum : uint32_t { INPUT_VIDEO=1, INPUT_AUDIO=2 };
enum : uint32_t { INPUT_RENDERED=1, INPUT_AUDIO_MIX=2, INPUT_GENERATED=3 };
#pragma pack(push,8)
struct SbrecExternalInputV1 {
    uint32_t size,version,track,provenance,width,height,strideBytes,sampleFrames;
    const void* data;
    uint32_t bytes,channels,rate,reserved;
    uint64_t captureQpc,durationNs; // video duration1..1s; audio duration must be0 (derived from sampleFrames)
};
struct SbrecExternalStatusV1 {
    uint32_t size,version,ready,reserved;
    uint64_t originQpc,frequency,acceptedVideo,acceptedAudio,droppedVideo,droppedAudio;
    uint64_t invalidInputs,processContentionReturns,observationDrops;
};
struct SbrecExternalObservationV1 {
    uint32_t size,version,track,provenance,result,discontinuity;
    uint64_t sequence,captureQpc,submissionQpc,ptsNs,durationNs,bytes;
};
#pragma pack(pop)
static_assert(sizeof(SbrecExternalInputV1)==72 && sizeof(SbrecExternalStatusV1)==88 && sizeof(SbrecExternalObservationV1)==72,"external input V1 x64 layout");
extern "C" {
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_abi_version() noexcept;
// Capability 1 requires an explicit trusted region copied synchronously at start.
// Additive query: all existing ABI layouts and existing export signatures remain intact.
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_trusted_region_version() noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_clock(SbrecClock*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_create(uint64_t*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_start(uint64_t,const SbrecRunSettings*,const SbrecCredentialUpdate*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_update_credentials(uint64_t,const SbrecCredentialUpdate*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_status(uint64_t,SbrecRunStatus*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_drain_observations(uint64_t,SbrecObservation*,uint32_t,uint32_t*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_request_stop(uint64_t) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_release(uint64_t) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_clock_v2_info(uint32_t*,uint32_t*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_enable_clock_v2(uint64_t,uint32_t,uint32_t,uint32_t,uint32_t) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_drain_clock_v2(uint64_t,FrameAnchorV2*,uint32_t,uint32_t,uint32_t*,uint64_t*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_external_v1_info(uint32_t*,uint32_t*,uint32_t*,uint32_t*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_enable_external_v1(uint64_t,uint32_t) noexcept;
// Valid borrowed input memory must remain readable until return. Native copies
// into a preallocated pool; no pointer is retained. BUSY means drop, never retry
// on an audio callback. RGBA8 top-to-bottom; PCM F32LE interleaved48000Hz/stereo.
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_push_external_v1(uint64_t,const SbrecExternalInputV1*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_external_status_v1(uint64_t,SbrecExternalStatusV1*) noexcept;
SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_drain_external_v1(uint64_t,SbrecExternalObservationV1*,uint32_t,uint32_t*) noexcept;
}

// Optional, additive configuration; valid only before start while CREATED.
extern "C" SBREC_RUNNER_EXPORT uint32_t __cdecl sbrec_runner_configure_media_v1(uint64_t,const SbrecMediaConfigV1*) noexcept;
