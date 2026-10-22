#include "native-media-clock.h"
#include <windows.h>
#include <algorithm>
#include <cstring>

namespace media_clock {
FrameAnchorV2 empty_record(uint32_t kind) noexcept {
    FrameAnchorV2 r{};r.size=sizeof(r);r.version=2;r.kind=kind;r.track=1;
    // Only optional u64 fields use all-ones. Identity/counter fields are set by
    // Engine; zero remains a real source/segment/running timestamp.
    uint64_t unknown=Unknown;
    for(size_t i=offsetof(FrameAnchorV2,sequence);i<sizeof(r);i+=8) memcpy(reinterpret_cast<char*>(&r)+i,&unknown,8);
    return r;
}
SegmentSample segment_at(GstPad* pad,uint64_t pts) noexcept {
    SegmentSample r;auto* event=gst_pad_get_sticky_event(pad,GST_EVENT_SEGMENT,0);
    if(!event)return r;
    const GstSegment* s;gst_event_parse_segment(event,&s);r.segment=*s;r.sequence=gst_event_get_seqnum(event);gst_event_unref(event);
    r.valid=s!=nullptr && r.segment.format==GST_FORMAT_TIME && r.segment.rate==1.0 && r.segment.applied_rate==1.0;
    for(auto n:{r.segment.start,r.segment.base,r.segment.time,r.segment.offset}) if(n==Unknown || n>G_MAXINT64)r.valid=false;
    if(r.valid && pts!=Unknown && pts<=G_MAXINT64) {
        uint64_t value=Unknown;
        if(gst_segment_to_running_time_full(&r.segment,GST_FORMAT_TIME,pts,&value)>0 && value<=G_MAXINT64)r.running=value;
    }
    return r;
}
Sample sample_source(GstElement* source) noexcept {
    Sample s;s.data=empty_record(CLOCK_SUPPORT);
    if(!source)return s;
    s.identity=gst_element_get_clock(source);
    if(!s.identity)return s;
    auto& r=s.data;
    const auto base=gst_element_get_base_time(source);
    GstClockTime ci,ce;GstClockTime rn,rd;
    gst_clock_get_calibration(s.identity,&ci,&ce,&rn,&rd);
    GstClockType type=GST_CLOCK_TYPE_REALTIME;
    if(GST_IS_SYSTEM_CLOCK(s.identity))g_object_get(s.identity,"clock-type",&type,nullptr);
    GstClock* master=gst_clock_get_master(s.identity);
    LARGE_INTEGER lo,hi,f;QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&lo);r.gstClockTimeNs=gst_clock_get_time(s.identity);QueryPerformanceCounter(&hi);
    r.qpcBefore=lo.QuadPart;r.qpcAfter=hi.QuadPart;r.qpcFrequency=f.QuadPart;
    r.pipelineBaseTimeNs=base;r.clockResolutionNs=gst_clock_get_resolution(s.identity);
    r.calibrationInternalNs=ci;r.calibrationExternalNs=ce;r.calibrationRateNumerator=rn;r.calibrationRateDenominator=rd;
    GstClockTime ci2,ce2,rn2,rd2;gst_clock_get_calibration(s.identity,&ci2,&ce2,&rn2,&rd2);
    GstClock* after=gst_element_get_clock(source);
    GstObject* parent=gst_object_get_parent(GST_OBJECT(source));
    GstClock* pipelineClock=parent && GST_IS_PIPELINE(parent) ? gst_element_get_clock(GST_ELEMENT(parent)) : nullptr;
    // Monotonic SystemClock only. The finite bound comes from clock ordering
    // between measured QPC enclosures, NOT assuming QPC and Gst share an origin.
    s.supported=GST_IS_SYSTEM_CLOCK(s.identity) && type==GST_CLOCK_TYPE_MONOTONIC && !master &&
        base!=Unknown && base<=G_MAXINT64 && gst_element_get_base_time(source)==base && after==s.identity &&
        parent && GST_IS_PIPELINE(parent) && gst_element_get_base_time(GST_ELEMENT(parent))==base && pipelineClock==s.identity &&
        ci==ci2 && ce==ce2 && rn==rn2 && rd==rd2 && rn && rd && r.clockResolutionNs!=Unknown &&
        r.qpcBefore<=r.qpcAfter && r.qpcFrequency && r.qpcFrequency<=1000000000 && r.gstClockTimeNs<=G_MAXINT64;
    if(after)gst_object_unref(after);if(master)gst_object_unref(master);
    if(pipelineClock)gst_object_unref(pipelineClock);if(parent)gst_object_unref(parent);
    return s; // caller releases identity after Engine has retained it if needed
}
Engine::Engine(uint32_t c,uint32_t i) noexcept:capacity(c),intervalMs(i) {}
Engine::~Engine() { if(clock_)gst_object_unref(clock_);if(source_)gst_object_unref(source_); }
void Engine::set_source(GstElement* e) noexcept { if(!source_)source_=GST_ELEMENT(gst_object_ref(e)); }
GstElement* Engine::source_ref() const noexcept { return source_ ? GST_ELEMENT(gst_object_ref(source_)) : nullptr; }
void Engine::push(FrameAnchorV2 r) noexcept {
    r.sequence=++sequence_;r.clockEpoch=epoch_;
    if(count_==capacity){++dropped_;return;}
    output_[(head_+count_)%capacity]=r;++count_;
}
void Engine::emit(Pending& p) noexcept {
    if(p.row.reason==CLOCK_VALID && (p.row.flags&(CLOCK_SOURCE|CLOCK_ENCODED|CLOCK_SUBMITTED|CLOCK_ESTIMATE))!=15)p.row.reason=CLOCK_UNMATCHED_END;
    if(p.row.reason!=CLOCK_VALID)p.row.flags&=~CLOCK_ESTIMATE;
    if(p.row.reason==CLOCK_VALID && capacity-count_>=3) {
        for(auto r:{p.lower,p.upper}) {r.kind=CLOCK_SUPPORT;r.frameOrdinal=p.row.frameOrdinal;r.producerEpoch=p.row.producerEpoch;push(r);}
    } else if(p.row.reason==CLOCK_VALID) {
        sequence_+=2;dropped_+=2;p.row.reason=CLOCK_OUTPUT_LOSS;p.row.flags&=~CLOCK_ESTIMATE;
    }
    if(!(p.row.flags&CLOCK_ESTIMATE))p.row.estimatedScheduleQpc=p.row.scheduleUncertaintyNs=Unknown;
    push(p.row);p.used=false;
}
void Engine::boundary(uint32_t reason) noexcept {
    for(auto& p:pending_)if(p.used){p.row.reason=reason;emit(p);}
    ++epoch_;historyHead_=historyCount_=0;next_=0;lastSource_=lastEncoded_=lastSubmitted_=Unknown;
    auto r=empty_record(CLOCK_BOUNDARY);r.reason=reason;push(r);
}
void Engine::loss(uint64_t n) noexcept {
    if(n!=lastLost_){if(!lossActive_)boundary(CLOCK_OBSERVATION_LOSS);lossActive_=true;lastLost_=n;}
    else lossActive_=false;
}
bool Engine::clock_sample(const Sample& s) noexcept {
    const auto& r=s.data;
    if(!s.identity || !s.supported || !r.qpcFrequency || r.qpcFrequency>1000000000 ||
        r.qpcBefore>r.qpcAfter || r.qpcAfter>G_MAXINT64 || r.gstClockTimeNs>G_MAXINT64 ||
        r.pipelineBaseTimeNs>G_MAXINT64 || r.clockResolutionNs>G_MAXINT64) {
        if(historyCount_)boundary(CLOCK_UNSUPPORTED);return false;
    }
    // Keep the last accepted QPC fence even when an epoch boundary cleared
    // history: an in-flight sampler from before reset must not restore it.
    if(clock_ && r.qpcAfter<latest_.qpcBefore)return false;
    if(clock_ && (clock_!=s.identity || latest_.pipelineBaseTimeNs!=r.pipelineBaseTimeNs ||
        latest_.calibrationInternalNs!=r.calibrationInternalNs || latest_.calibrationExternalNs!=r.calibrationExternalNs ||
        latest_.calibrationRateNumerator!=r.calibrationRateNumerator || latest_.calibrationRateDenominator!=r.calibrationRateDenominator))boundary(CLOCK_CHANGED);
    if(clock_!=s.identity){if(clock_)gst_object_unref(clock_);clock_=GST_CLOCK(gst_object_ref(s.identity));}
    if(historyCount_ && r.gstClockTimeNs<latest_.gstClockTimeNs) {
        if(r.qpcBefore>latest_.qpcAfter)boundary(CLOCK_CHANGED);else return true;
    }
    latest_=r;
    if(historyCount_==history_.size()){historyHead_=(historyHead_+1)%history_.size();--historyCount_;}
    history_[(historyHead_+historyCount_)%history_.size()]=r;++historyCount_;return true;
}
bool Engine::source_segment(const SegmentSample& s) noexcept {
    bool changed=sourceSegment_.sequence && (sourceSegment_.sequence!=s.sequence || !gst_segment_is_equal(&sourceSegment_.segment,&s.segment));
    if(changed)boundary(CLOCK_SEGMENT_CHANGED);
    if(lastSource_!=Unknown && (s.running==Unknown || s.running<lastSource_))boundary(s.running==Unknown?CLOCK_UNKNOWN:CLOCK_SEGMENT_CHANGED);
    else if(s.running!=Unknown && s.running==lastSource_)
        for(auto& p:pending_)if(p.used && p.row.sourceRunningNs==s.running)p.row.reason=CLOCK_AMBIGUOUS;
    sourceSegment_=s;lastSource_=s.running;complete();return s.valid;
}
bool Engine::wants(uint64_t running) noexcept {
    if(running==Unknown){if(next_==Unknown)return false;next_=Unknown;return true;}
    if(next_==Unknown)next_=0;
    if(running<next_)return false;
    next_=running>G_MAXINT64-intervalMs*1000000ULL ? Unknown : running+intervalMs*1000000ULL;return true;
}
void Engine::source(uint64_t ordinal,uint64_t pts,uint64_t duration,const SegmentSample& seg,uint64_t arrival,
                    const Sample& sample,uint64_t producerEpoch,uint64_t lost) noexcept {
    loss(lost);
    Pending* p=nullptr;for(auto& candidate:pending_)if(!candidate.used){p=&candidate;break;}
    if(!p){auto r=empty_record();r.frameOrdinal=ordinal;r.reason=CLOCK_PENDING_LIMIT;push(r);return;}
    *p=Pending{};p->used=true;p->row=sample.data;
    auto& r=p->row;r.kind=CLOCK_FRAME;r.flags=CLOCK_SOURCE;r.frameOrdinal=ordinal;r.clockEpoch=epoch_;r.producerEpoch=producerEpoch;
    r.sourcePtsNs=pts;r.sourceDurationNs=duration;r.sourceRunningNs=seg.running;r.sourceSegmentStartNs=seg.segment.start;
    r.sourceSegmentBaseNs=seg.segment.base;r.sourceSegmentSeq=seg.sequence;r.padArrivalQpc=arrival;
    if(lossActive_)r.reason=CLOCK_OBSERVATION_LOSS;
    else if(!seg.valid)r.reason=CLOCK_SEGMENT;
    else if(seg.running==Unknown || pts==Unknown)r.reason=CLOCK_UNKNOWN;
    else if(!sample.supported || !sample.identity)r.reason=CLOCK_UNSUPPORTED;
    else if(sample.data.pipelineBaseTimeNs>G_MAXINT64-seg.running)r.reason=CLOCK_ARITHMETIC;
    else if(sample.identity!=clock_ || sample.data.pipelineBaseTimeNs!=latest_.pipelineBaseTimeNs ||
        sample.data.calibrationInternalNs!=latest_.calibrationInternalNs || sample.data.calibrationExternalNs!=latest_.calibrationExternalNs ||
        sample.data.calibrationRateNumerator!=latest_.calibrationRateNumerator || sample.data.calibrationRateDenominator!=latest_.calibrationRateDenominator)r.reason=CLOCK_CHANGED;
    else {
        const auto target=sample.data.pipelineBaseTimeNs+seg.running;
        bool lo=false,hi=false;
        for(uint32_t i=0;i<historyCount_;i++) {
            const auto& h=history_[(historyHead_+i)%history_.size()];
            if(h.gstClockTimeNs<target && (!lo || h.gstClockTimeNs>p->lower.gstClockTimeNs)){p->lower=h;lo=true;}
            if(h.gstClockTimeNs>target && (!hi || h.gstClockTimeNs<p->upper.gstClockTimeNs)){p->upper=h;hi=true;}
        }
        if(!lo || !hi || p->lower.qpcBefore>p->upper.qpcAfter)r.reason=CLOCK_NO_BRACKET;
        else {
            // An interval estimate needs only strict monotonic ordering. The
            // two support records make this finite enclosure independently
            // auditable, including delayed-pad and non-uniform clock timing.
            const auto width=p->upper.qpcAfter-p->lower.qpcBefore;
            r.estimatedScheduleQpc=p->lower.qpcBefore+width/2;
            auto ns=gst_util_uint64_scale_ceil((width+1)/2,GST_SECOND,r.qpcFrequency);
            if(ns>G_MAXINT64-r.clockResolutionNs)r.reason=CLOCK_ARITHMETIC;
            else {r.scheduleUncertaintyNs=ns+r.clockResolutionNs;r.flags|=CLOCK_ESTIMATE;}
        }
    }
    if(r.reason!=CLOCK_VALID){r.estimatedScheduleQpc=r.scheduleUncertaintyNs=Unknown;}
}
void Engine::encoded(uint64_t pts,const SegmentSample& s,uint64_t lost) noexcept {
    loss(lost);
    if(s.running==Unknown || !s.valid){boundary(CLOCK_UNKNOWN);lastEncoded_=Unknown;return;}
    if(encodedSegment_.sequence && (encodedSegment_.sequence!=s.sequence || !gst_segment_is_equal(&encodedSegment_.segment,&s.segment)))boundary(CLOCK_SEGMENT_CHANGED);
    encodedSegment_=s;
    if(lastEncoded_!=Unknown && s.running!=Unknown && s.running<lastEncoded_)boundary(CLOCK_SEGMENT_CHANGED);
    for(auto& p:pending_)if(p.used && p.row.sourceRunningNs==s.running && s.running!=Unknown) {
        if(++p.encoded>1)p.row.reason=CLOCK_AMBIGUOUS;
        p.row.flags|=CLOCK_ENCODED;p.row.encodedPtsNs=pts;p.row.encodedRunningNs=s.running;
        p.row.encodedSegmentStartNs=s.segment.start;p.row.encodedSegmentBaseNs=s.segment.base;p.row.encodedSegmentSeq=s.sequence;
        if(!s.valid)p.row.reason=CLOCK_SEGMENT;
    }
    lastEncoded_=s.running;complete();
}
void Engine::submitted(uint64_t running,uint64_t sdkPts,uint32_t flags,uint64_t producerEpoch,uint64_t lost) noexcept {
    loss(lost);
    if(running==Unknown || sdkPts==Unknown){boundary(CLOCK_UNKNOWN);lastSubmitted_=Unknown;return;}
    if(sdkPts>G_MAXINT64/100){boundary(CLOCK_ARITHMETIC);return;}
    if(lastSubmitted_!=Unknown && running!=Unknown && running<lastSubmitted_)boundary(CLOCK_PRODUCER_RESET);
    for(auto& p:pending_)if(p.used && p.row.sourceRunningNs==running && running!=Unknown) {
        if(++p.submitted>1)p.row.reason=CLOCK_AMBIGUOUS;
        p.row.flags|=CLOCK_SUBMITTED;if(flags&1)p.row.flags|=CLOCK_KEY;
        p.row.sdkInputRunningNs=running;p.row.sdkPts100ns=sdkPts;
        if(p.row.producerEpoch!=producerEpoch)p.row.reason=CLOCK_PRODUCER_RESET;
    }
    lastSubmitted_=running;complete();
}
void Engine::complete(bool final) noexcept {
    for(auto& p:pending_)if(p.used && (final || (p.encoded && p.submitted && lastSource_!=Unknown && lastEncoded_!=Unknown && lastSubmitted_!=Unknown && lastSource_>p.row.sourceRunningNs && lastEncoded_>p.row.sourceRunningNs && lastSubmitted_>p.row.sourceRunningNs)))emit(p);
}
void Engine::finish() noexcept { complete(true); }
uint32_t Engine::drain(FrameAnchorV2* out,uint32_t capacity) noexcept {
    uint32_t n=0;while(n<capacity && count_){out[n++]=output_[head_];head_=(head_+1)%this->capacity;--count_;}return n;
}
}
