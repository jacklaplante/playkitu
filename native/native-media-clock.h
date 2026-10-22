#pragma once
#include "native-synthetic-runner.h"
#include <array>

namespace media_clock {
constexpr uint64_t Unknown=UINT64_MAX;
FrameAnchorV2 empty_record(uint32_t kind=CLOCK_FRAME) noexcept;
struct SegmentSample { GstSegment segment{}; uint32_t sequence=0; bool valid=false; uint64_t running=Unknown; };
SegmentSample segment_at(GstPad*,uint64_t) noexcept;
struct Sample { FrameAnchorV2 data; GstClock* identity=nullptr; bool supported=false; };
Sample sample_source(GstElement*) noexcept;

// All methods except sample_source/segment_at run under the owning Run mutex.
// Fixed history holds ~2.56s at the existing 10ms monitor cadence. No history is
// extrapolated: a selected scheduling instant needs strict surrounding samples.
class Engine {
public:
    const uint32_t capacity,intervalMs;
    explicit Engine(uint32_t capacity,uint32_t intervalMs) noexcept;
    ~Engine();
    void set_source(GstElement*) noexcept;
    GstElement* source_ref() const noexcept;
    bool clock_sample(const Sample&) noexcept;
    bool source_segment(const SegmentSample&) noexcept;
    bool wants(uint64_t) noexcept;
    void source(uint64_t ordinal,uint64_t pts,uint64_t duration,const SegmentSample&,uint64_t arrival,
                const Sample&,uint64_t producerEpoch,uint64_t lost) noexcept;
    void encoded(uint64_t pts,const SegmentSample&,uint64_t lost) noexcept;
    void submitted(uint64_t running,uint64_t sdkPts,uint32_t flags,uint64_t producerEpoch,uint64_t lost) noexcept;
    void boundary(uint32_t reason) noexcept;
    void finish() noexcept;
    uint32_t drain(FrameAnchorV2*,uint32_t) noexcept;
    uint64_t dropped() const noexcept { return dropped_; }
private:
    struct Pending { FrameAnchorV2 row,lower,upper; uint32_t encoded=0,submitted=0; bool used=false; };
    std::array<Pending,16> pending_{};
    std::array<FrameAnchorV2,64> output_{};
    std::array<FrameAnchorV2,256> history_{};
    uint32_t head_=0,count_=0,historyHead_=0,historyCount_=0;
    uint64_t sequence_=0,dropped_=0,epoch_=1,next_=0,lastLost_=0;
    uint64_t lastSource_=Unknown,lastEncoded_=Unknown,lastSubmitted_=Unknown;
    bool lossActive_=false;
    GstElement* source_=nullptr; GstClock* clock_=nullptr;
    FrameAnchorV2 latest_{};
    SegmentSample sourceSegment_{},encodedSegment_{};
    void loss(uint64_t) noexcept;
    void push(FrameAnchorV2) noexcept;
    void emit(Pending&) noexcept;
    void complete(bool final=false) noexcept;
};
}
