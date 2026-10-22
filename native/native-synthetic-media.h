#include "native-media-config.h"
#pragma once
#include <gst/gst.h>
#include <atomic>
#include <string>

// Shared B0/runner generated media and segment-based timestamp observations.
// Readers inspect Samples only after NULL state has quiesced streaming probes.
struct Samples {
    const char* name;
    guint64 count = 0, bytes = 0, first = GST_CLOCK_TIME_NONE, last = 0;
    guint64 previousKey = GST_CLOCK_TIME_NONE, maxKeyGap = 0, keys = 0;
    bool monotonic = true;
    std::string caps;
    GstSegment segment{};
    guint64 firstRunning = GST_CLOCK_TIME_NONE, lastRunning = 0;
    bool runningMonotonic = true;
};
GstPadProbeReturn observe(GstPad*, GstPadProbeInfo*, gpointer);
std::string synthetic_media(int seconds, int width, int height, int fps,
                            const char* destination, bool counter, const SbrecMediaConfigV1* options = nullptr);
std::string external_media(int width, int height, int fps, const char* destination, const SbrecMediaConfigV1* options = nullptr);
extern std::atomic<bool> sbrec_native_owned;

// kvssink / KVS FrameOrderCoordinator compare PTS on this owned runner's 1ms
// timecode grid, but ContentView checks relative DTS. Quantize only the common
// UTC translation, so equal source PTS/DTS cannot acquire different bin origins.
constexpr uint32_t SbrecSdkTimecodeScaleMs = 1;
constexpr uint64_t SbrecSdkUtcQuantization100ns = uint64_t(SbrecSdkTimecodeScaleMs) * 10000;
inline bool sbrec_sdk_utc_pts_ns(uint64_t utcNs, uint64_t firstPtsNs, uint64_t framePtsNs,
                                uint64_t gridNs, uint64_t& translatedNs) noexcept {
    // kvssink subsequently constructs signed chrono::nanoseconds. Also exclude
    // GST_CLOCK_TIME_NONE rather than translating its UINT64_MAX sentinel.
    if (!gridNs || utcNs > INT64_MAX || firstPtsNs > INT64_MAX || framePtsNs > INT64_MAX || utcNs < firstPtsNs) return false;
    const uint64_t offset = utcNs - firstPtsNs;
    const uint64_t aligned = offset - offset % gridNs;
    if (framePtsNs > uint64_t(INT64_MAX) - aligned) return false;
    translatedNs = framePtsNs + aligned;
    return true;
}

// Private synchronous plugin observation ABI. The SDK owns every borrowed
// pointer; the observer copies a fixed POD before returning. No managed callback.
enum : uint32_t { SBREC_FRAME = 1, SBREC_ACK = 2, SBREC_RESET = 3, SBREC_GAP = 4 };
struct SbrecSdkEvent {
    uint32_t kind = 0, track = 0, flags = 0, result = 0;
    uint64_t inputRunningNs = GST_CLOCK_TIME_NONE, sdkPts100ns = 0;
    uint64_t upload = 0, timecodeMs = 0, bytes = 0;
    char fragment[129]{};
};
struct SbrecSdkObserver {
    void (*callback)(void*, const SbrecSdkEvent*) noexcept;
    bool (*cancelled)(void*) noexcept;
    void* context;
};
inline bool sbrec_cancelled(GObject* sink) noexcept {
    auto* observer = static_cast<SbrecSdkObserver*>(g_object_get_data(sink, "sbrec-observer-v1"));
    return observer && observer->cancelled(observer->context);
}
#ifdef SBREC_TEST_TRANSPORT
extern "C" void sbrec_test_lifecycle_event(uint32_t);
#endif
inline void sbrec_emit(GObject* sink, const SbrecSdkEvent& event) noexcept {
#ifdef SBREC_TEST_TRANSPORT
    // Real intercepted SDK ACK callback held across shutdown by the isolated C7
    // regression. No test gate/export is present in the production plugin.
    if (event.kind == SBREC_ACK) sbrec_test_lifecycle_event(1);
#endif
    auto* observer = static_cast<SbrecSdkObserver*>(g_object_get_data(sink, "sbrec-observer-v1"));
    if (observer) observer->callback(observer->context, &event);
#ifdef SBREC_TEST_TRANSPORT
    if (event.kind == SBREC_ACK) sbrec_test_lifecycle_event(2);
#endif
}
