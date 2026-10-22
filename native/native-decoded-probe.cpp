// B2e offline evidence only. No SDK sink, capture, output device or network URI.
#include <gst/gst.h>
#include <windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <string>
#include <vector>

namespace decoded_probe {
constexpr guint64 Unknown=GST_CLOCK_TIME_NONE, MaxTime=G_MAXINT64, MaxFile=64ULL*1024*1024;
constexpr size_t MaxOutput=2*1024*1024;
// A 20s batch can expose ~3076 separate video/AAC demux+decoded spans at
// 30fps/48kHz. Keep exact holes/durations; bound records instead of merging them.
constexpr uint32_t MaxSpans=4096;
struct Budget {
    uint32_t maxSpans=MaxSpans, spans=0, transitions=0;
    uint64_t buffers=0, bytes=0;
    std::atomic<bool> stopped{false};
    std::string reason;
    void fail(const char* code) { if(reason.empty()) reason=code; stopped=true; }
    bool transition() { if(++transitions>4096) { fail("transition_limit"); return false; } return true; }
};
guint64 mapped(const GstSegment& s,guint64 pts,bool stream) {
    if(s.format!=GST_FORMAT_TIME || pts==Unknown || pts>MaxTime || s.start==Unknown || s.base==Unknown || s.time==Unknown) return Unknown;
    guint64 result=Unknown;
    const int sign=stream ? gst_segment_to_stream_time_full(&s,GST_FORMAT_TIME,pts,&result)
                          : gst_segment_to_running_time_full(&s,GST_FORMAT_TIME,pts,&result);
    return sign>0 && result<=MaxTime ? result : Unknown;
}
struct Span {
    std::string reason;
    uint32_t segment=0; uint64_t buffers=0;
    guint64 firstPts=Unknown,lastPts=Unknown,firstRunning=Unknown,lastRunning=Unknown;
    guint64 firstStream=Unknown,lastStream=Unknown,lastDuration=Unknown;
    guint64 endPts=Unknown,endRunning=Unknown,endStream=Unknown,previousEndPts=Unknown;
};
struct Segment { GstSegment value; guint32 sequence; };
struct Transition { const char* kind; guint64 pts,duration; uint32_t segment; };
struct Selection {
    guint64 target=0,tolerance=0;uint32_t matches=0,segment=0;
    guint64 ordinal=Unknown,pts=Unknown,running=Unknown,stream=Unknown,duration=Unknown;
    void observe(uint64_t n,uint32_t seg,uint64_t p,uint64_t r,uint64_t s,uint64_t d) noexcept {
        if(p==Unknown || (p>target ? p-target : target-p)>tolerance)return;
        if(++matches==1){ordinal=n;segment=seg;pts=p;running=r;stream=s;duration=d;}
    }
};
struct Stage {
    Budget* budget;
    std::vector<Segment> segments;
    std::vector<Transition> transitions;
    std::vector<Span> spans;
    std::string boundary="first";
    std::string caps;
    uint64_t buffers=0,overlaps=0;
    bool eos=false;
    std::vector<Selection>* selections=nullptr; // decoded video only, fixed <=64 queries
    explicit Stage(Budget* b):budget(b) {}
    void segment(const GstSegment& s,guint32 sequence) {
        if(!segments.empty() && segments.back().sequence==sequence && gst_segment_is_equal(&segments.back().value,&s)) return;
        if(!budget->transition()) return;
        if(s.format!=GST_FORMAT_TIME) { budget->fail("unsupported_segment_format"); return; }
        if(s.rate!=1.0 || s.applied_rate!=1.0) { budget->fail("unsupported_segment_rate"); return; }
        for(auto n:{s.start,s.stop,s.time,s.base,s.offset,s.position,s.duration}) {
            if(n!=Unknown && n>MaxTime) { budget->fail("segment_timestamp_overflow"); return; }
        }
        segments.push_back({s,sequence}); boundary=segments.size()==1 ? "first_segment" : "segment_change";
    }
    void event(const char* kind,guint64 pts=Unknown,guint64 duration=Unknown) {
        if(!budget->transition()) return;
        transitions.push_back({kind,pts,duration,static_cast<uint32_t>(segments.size())});
        boundary=kind;
    }
    void buffer(guint64 pts,guint64 duration,guint flags,size_t bytes) {
        if(budget->stopped) return;
        if(++budget->buffers>200000 || bytes>8*1024*1024 || budget->bytes>64ULL*1024*1024*1024-bytes) { budget->fail("decoded_work_limit"); return; }
        budget->bytes+=bytes;
        if((pts!=Unknown && pts>MaxTime) || (duration!=Unknown && duration>MaxTime) ||
            (pts!=Unknown && duration!=Unknown && duration>MaxTime-pts)) { budget->fail("timestamp_overflow"); return; }
        if(flags&GST_BUFFER_FLAG_DISCONT) event("discont",pts,duration);
        if(flags&GST_BUFFER_FLAG_RESYNC) event("resync",pts,duration);
        if(flags&GST_BUFFER_FLAG_GAP) event("gap_buffer",pts,duration);
        if(flags&GST_BUFFER_FLAG_CORRUPTED) { event("corrupted_buffer",pts,duration); budget->fail("corrupted_buffer"); return; }
        const GstSegment* s=segments.empty() ? nullptr : &segments.back().value;
        guint64 running=s ? mapped(*s,pts,false) : Unknown, stream=s ? mapped(*s,pts,true) : Unknown;
        guint64 end=(pts==Unknown || duration==Unknown) ? Unknown : pts+duration;
        std::string reason=boundary;
        guint64 priorEnd=spans.empty() ? Unknown : spans.back().endPts;
        if(reason.empty() && !spans.empty()) {
            const auto& previous=spans.back();
            if(pts==Unknown || running==Unknown || stream==Unknown) reason="unknown_timestamp";
            else if(previous.lastPts==Unknown || previous.endPts==Unknown || previous.endRunning==Unknown) reason="unknown_previous_duration_or_time";
            else if(pts<previous.lastPts || running<previous.lastRunning || stream<previous.lastStream) reason="timestamp_regression";
            else if(pts>previous.endPts || running>previous.endRunning || stream>previous.endStream) reason="timestamp_hole";
            else if(pts<previous.endPts) ++overlaps;
        }
        if(spans.empty() || !reason.empty()) {
            if(budget->spans==budget->maxSpans) { budget->fail("span_limit"); return; }
            ++budget->spans;
            Span next; next.reason=reason.empty() ? "first" : reason;
            next.segment=static_cast<uint32_t>(segments.size()); next.previousEndPts=priorEnd;
            next.firstPts=pts; next.firstRunning=running; next.firstStream=stream;
            spans.push_back(next);
        }
        auto& span=spans.back(); ++span.buffers; ++buffers;
        span.lastPts=pts; span.lastRunning=running; span.lastStream=stream; span.lastDuration=duration;
        // Preserve the observed union of overlapping buffers. Unknown final
        // duration is not replaced with a guessed FPS/AAC duration.
        span.endPts=end==Unknown ? Unknown : (std::max)(span.endPts==Unknown ? 0 : span.endPts,end);
        span.endRunning=(s && span.endPts!=Unknown) ? mapped(*s,span.endPts,false) : Unknown;
        span.endStream=(s && span.endPts!=Unknown) ? mapped(*s,span.endPts,true) : Unknown;
        if(selections && !(flags&(GST_BUFFER_FLAG_GAP|GST_BUFFER_FLAG_CORRUPTED)))
            for(auto& q:*selections)q.observe(buffers,static_cast<uint32_t>(segments.size()),pts,running,stream,duration);
        boundary.clear();
    }
    // Sticky segment is inspected on EVERY buffer, as well as on events. This
    // also detects a changed segment/sequence even if an earlier event was missed.
    void pad_buffer(GstPad* pad,GstBuffer* buffer) {
        GstEvent* e=gst_pad_get_sticky_event(pad,GST_EVENT_SEGMENT,0);
        if(e) { const GstSegment* s; gst_event_parse_segment(e,&s); segment(*s,gst_event_get_seqnum(e)); gst_event_unref(e); }
        if(caps.empty()) {
            GstCaps* c=gst_pad_get_current_caps(pad); gchar* value=c ? gst_caps_to_string(c) : nullptr;
            if(value && strlen(value)<=1024 && g_utf8_validate(value,-1,nullptr)) caps=value;
            else budget->fail("caps_missing_or_limit");
            g_free(value); if(c) gst_caps_unref(c);
        }
        this->buffer(GST_BUFFER_PTS(buffer),GST_BUFFER_DURATION(buffer),GST_BUFFER_FLAGS(buffer),gst_buffer_get_size(buffer));
    }
};
struct Track {
    const char* kind; std::string padName,streamId;
    Stage demux,decoded;
    explicit Track(const char* k,Budget* b):kind(k),demux(b),decoded(b) {}
};
struct Probe {
    std::mutex mutex; Budget budget;
    Track video{"video",&budget},audio{"audio",&budget};
    GstElement* pipeline=nullptr;
    std::vector<Selection> selections;
    Probe(){video.decoded.selections=&selections;}
};
struct Tap { Probe* probe; Stage* stage; };
GstPadProbeReturn tap(GstPad* pad,GstPadProbeInfo* info,gpointer data) noexcept {
    auto& t=*static_cast<Tap*>(data);
    try {
        std::lock_guard<std::mutex> lock(t.probe->mutex);
        if(t.probe->budget.stopped) return GST_PAD_PROBE_DROP;
        if(GST_PAD_PROBE_INFO_TYPE(info)&GST_PAD_PROBE_TYPE_BUFFER) t.stage->pad_buffer(pad,GST_PAD_PROBE_INFO_BUFFER(info));
        else {
            GstEvent* event=GST_PAD_PROBE_INFO_EVENT(info);
            switch(GST_EVENT_TYPE(event)) {
                case GST_EVENT_SEGMENT: { const GstSegment* s; gst_event_parse_segment(event,&s); t.stage->segment(*s,gst_event_get_seqnum(event)); break; }
                case GST_EVENT_GAP: { guint64 pts,duration; gst_event_parse_gap(event,&pts,&duration); t.stage->event("gap_event",pts,duration); break; }
                case GST_EVENT_FLUSH_START: t.stage->event("flush_start"); break;
                case GST_EVENT_FLUSH_STOP: t.stage->event("flush_stop"); break;
                case GST_EVENT_EOS: t.stage->eos=true; break;
                default: break;
            }
        }
    } catch(...) { std::lock_guard<std::mutex> lock(t.probe->mutex); t.probe->budget.fail("probe_callback_failed"); }
    return GST_PAD_PROBE_OK;
}
void attach(GstPad* pad,Probe* p,Stage* s) {
    auto* context=new Tap{p,s};
    gst_pad_add_probe(pad,static_cast<GstPadProbeType>(GST_PAD_PROBE_TYPE_BUFFER|GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM|GST_PAD_PROBE_TYPE_EVENT_FLUSH),tap,context,[](gpointer x){delete static_cast<Tap*>(x);});
}
void pad_added(GstElement*,GstPad* pad,gpointer data) noexcept {
    auto& p=*static_cast<Probe*>(data);
    try {
        GstCaps* caps=gst_pad_get_current_caps(pad);
        const gchar* type=caps ? gst_structure_get_name(gst_caps_get_structure(caps,0)) : "";
        int mpegVersion=0;
        if(caps)gst_structure_get_int(gst_caps_get_structure(caps,0),"mpegversion",&mpegVersion);
        Track* track=!strcmp(type,"video/x-h264") ? &p.video : (!strcmp(type,"audio/mpeg") && mpegVersion==4) ? &p.audio : nullptr;
        if(caps) gst_caps_unref(caps);
        { std::lock_guard<std::mutex> lock(p.mutex);
          if(!track || !track->padName.empty()) { p.budget.fail("unsupported_or_duplicate_track"); return; }
          track->padName=GST_PAD_NAME(pad);
          gchar* id=gst_pad_get_stream_id(pad);
          if(id && strlen(id)<=256 && g_utf8_validate(id,-1,nullptr)) track->streamId=id;
          else p.budget.fail("stream_identity_missing_or_limit");
          g_free(id); }
        GError* error=nullptr;
        const char* branch=track==&p.video ? "queue max-size-buffers=4 max-size-bytes=0 max-size-time=0 ! h264parse ! d3d11h264dec ! identity name=decoded ! fakesink sync=false async=false"
            : "queue max-size-buffers=4 max-size-bytes=0 max-size-time=0 ! aacparse ! mfaacdec ! identity name=decoded ! fakesink sync=false async=false";
        GstElement* bin=gst_parse_bin_from_description(branch,TRUE,&error);
        if(error || !bin) { if(error) g_error_free(error); if(bin) gst_object_unref(bin); std::lock_guard<std::mutex> lock(p.mutex); p.budget.fail("decoder_unavailable"); return; }
        GstElement* identity=gst_bin_get_by_name(GST_BIN(bin),"decoded");
        GstPad* output=gst_element_get_static_pad(identity,"src"); attach(output,&p,&track->decoded);
        gst_object_unref(output); gst_object_unref(identity);
        attach(pad,&p,&track->demux);
        gst_bin_add(GST_BIN(p.pipeline),bin);
        GstPad* input=gst_element_get_static_pad(bin,"sink");
        const bool linked=gst_pad_link(pad,input)==GST_PAD_LINK_OK; gst_object_unref(input);
        if(!linked || !gst_element_sync_state_with_parent(bin)) { std::lock_guard<std::mutex> lock(p.mutex); p.budget.fail("decoder_link_or_state_failed"); }
    } catch(...) { std::lock_guard<std::mutex> lock(p.mutex); p.budget.fail("track_callback_failed"); }
}
std::string quoted(const std::string& value) {
    std::ostringstream out; out<<'"';
    for(unsigned char c:value) {
        if(c=='"' || c=='\\') out<<'\\'<<c;
        else if(c<32) out<<"\\u00"<<std::hex<<std::setw(2)<<std::setfill('0')<<static_cast<unsigned>(c)<<std::dec;
        else out<<c;
    }
    out<<'"'; return out.str();
}
// Integers are decimal STRINGS so unsigned64 timestamps survive ordinary JSON
// parsers without a floating-point round trip. GST_CLOCK_TIME_NONE is null.
std::string integer(guint64 n) { return n==Unknown ? "null" : quoted(std::to_string(n)); }
void stage_json(std::ostream& o,const Stage& s) {
    const Span empty;
    const auto& first=s.spans.empty()?empty:s.spans.front();
    const auto& last=s.spans.empty()?empty:s.spans.back();
    o<<"{\"buffers\":"<<integer(s.buffers)<<",\"firstPtsNs\":"<<integer(first.firstPts)<<",\"lastPtsNs\":"<<integer(last.lastPts)
     <<",\"firstRunningNs\":"<<integer(first.firstRunning)<<",\"lastRunningNs\":"<<integer(last.lastRunning)
     <<",\"lastBufferDurationNs\":"<<integer(last.lastDuration)<<",\"eosObserved\":"<<(s.eos?"true":"false")<<",\"caps\":"<<quoted(s.caps)
     <<",\"overlappingBuffers\":"<<integer(s.overlaps)<<",\"segments\":[";
    bool comma=false;
    for(const auto& e:s.segments) {
        if(comma) o<<','; comma=true; const auto& v=e.value;
        o<<"{\"sequence\":"<<e.sequence<<",\"format\":"<<static_cast<int>(v.format)<<",\"flags\":"<<static_cast<unsigned>(v.flags)
         <<",\"rate\":"<<std::setprecision(17)<<v.rate<<",\"appliedRate\":"<<v.applied_rate;
        for(auto pair:{std::make_pair("startNs",v.start),{"stopNs",v.stop},{"baseNs",v.base},{"offsetNs",v.offset},{"timeNs",v.time},{"positionNs",v.position},{"durationNs",v.duration}}) o<<','<<quoted(pair.first)<<':'<<integer(pair.second);
        o<<'}';
    }
    o<<"],\"transitions\":["; comma=false;
    for(const auto& e:s.transitions) { if(comma)o<<',';comma=true; o<<"{\"kind\":"<<quoted(e.kind)<<",\"segmentIndex\":"<<e.segment<<",\"ptsNs\":"<<integer(e.pts)<<",\"durationNs\":"<<integer(e.duration)<<'}'; }
    o<<"],\"spans\":["; comma=false;
    for(const auto& v:s.spans) {
        if(comma)o<<',';comma=true;
        o<<"{\"reason\":"<<quoted(v.reason)<<",\"segmentIndex\":"<<v.segment<<",\"buffers\":"<<integer(v.buffers);
        for(auto pair:{std::make_pair("firstPtsNs",v.firstPts),{"lastPtsNs",v.lastPts},{"firstRunningNs",v.firstRunning},{"lastRunningNs",v.lastRunning},
            {"firstStreamNs",v.firstStream},{"lastStreamNs",v.lastStream},{"lastBufferDurationNs",v.lastDuration},{"observedEndPtsNs",v.endPts},
            {"observedEndRunningNs",v.endRunning},{"observedEndStreamNs",v.endStream},{"previousEndPtsNs",v.previousEndPts}}) o<<','<<quoted(pair.first)<<':'<<integer(pair.second);
        o<<'}';
    }
    o<<"]}";
}
std::string result_json(const Probe& p,const std::string& sha,guint64 bytes,bool eos,const std::string& error,size_t outputLimit) {
    const bool observed=p.video.decoded.buffers || p.audio.decoded.buffers;
    const bool success=eos && observed && error.empty() && p.budget.reason.empty();
    std::ostringstream o;
    o<<"{\"schema\":1,\"kind\":\"playreel.decoded-media-probe\",\"timeUnit\":\"nanoseconds\",\"integerEncoding\":\"decimal-string-or-null\",\"fileSha256\":"<<(sha.empty()?"null":quoted(sha))
     <<",\"fileBytes\":"<<integer(bytes)<<",\"decodeEos\":"<<(eos?"true":"false")<<",\"status\":"<<quoted(success?"decoded":"incomplete")
     <<",\"reason\":"<<quoted(!p.budget.reason.empty()?p.budget.reason:error)<<",\"sourceOpeningKnown\":false,\"sourceTailKnown\":false,\"fileCompleteness\":\"unknown\",\"continuousCoverageClaim\":false,\"timestampHoleCause\":\"unverified_including_quantization\""
     <<",\"producerAnchor\":{\"available\":false,\"reason\":\"no_verified_producer_origin_or_frame_correspondence\",\"playbackOffsetVerified\":false}"
     <<",\"limits\":{\"maxInputBytes\":\"67108864\",\"maxDecodedBuffers\":\"200000\",\"maxSpans\":"<<p.budget.maxSpans<<",\"maxTransitions\":4096,\"maxOutputBytes\":"<<outputLimit<<"},\"tracks\":[";
    bool comma=false;
    for(const auto* t:{&p.video,&p.audio}) {
        if(t->padName.empty())continue;
        if(comma)o<<',';comma=true;
        o<<"{\"kind\":"<<quoted(t->kind)<<",\"demuxPad\":"<<quoted(t->padName)<<",\"streamId\":"<<quoted(t->streamId)<<",\"mkvTrackNumber\":null,\"demux\":";
        stage_json(o,t->demux);o<<",\"decoded\":";stage_json(o,t->decoded);o<<'}';
    }
    o<<']';
    if(!p.selections.empty()) {
        o<<",\"frameSelections\":{\"schema\":1,\"scope\":\"observed_file\",\"queries\":[";
        uint32_t id=0;
        for(const auto& q:p.selections) {
            if(id)o<<',';
            const bool unique=success && p.video.decoded.eos && q.matches==1 && q.running!=Unknown && q.stream!=Unknown;
            const char* status=!(success && p.video.decoded.eos) ? "incomplete" : q.matches>1 ? "ambiguous" : unique ? "unique_observed" : "missing";
            o<<"{\"queryId\":"<<++id<<",\"targetPtsNs\":"<<integer(q.target)<<",\"toleranceNs\":"<<integer(q.tolerance)
             <<",\"matchCount\":"<<integer(q.matches)<<",\"status\":"<<quoted(status)<<",\"segmentIndex\":"<<(unique ? std::to_string(q.segment) : "null");
            for(auto pair:{std::make_pair("bufferOrdinal",q.ordinal),{"ptsNs",q.pts},{"runningNs",q.running},{"streamNs",q.stream},{"durationNs",q.duration}})
                o<<','<<quoted(pair.first)<<':'<<integer(unique ? pair.second : Unknown);
            o<<'}';
        }
        o<<"]}";
    }
    o<<"}\n";
    auto text=o.str();
    if(text.size()>outputLimit) return "{\"schema\":1,\"kind\":\"playreel.decoded-media-probe\",\"fileSha256\":"+quoted(sha)+",\"status\":\"incomplete\",\"reason\":\"output_limit\",\"decodeEos\":"+(eos?"true":"false")+",\"continuousCoverageClaim\":false,\"producerAnchor\":{\"available\":false,\"playbackOffsetVerified\":false},\"tracks\":[]}\n";
    return text;
}
}

#ifndef SBREC_DECODED_PROBE_TEST
int wmain(int argc,wchar_t** argv) {
    using namespace decoded_probe;
    auto number=[](const wchar_t* x,long low,long high)->long { wchar_t* end; long n=wcstol(x,&end,10); return *x && !*end && n>=low && n<=high ? n : -1; };
    if(argc<3 || (argc>6 && argc!=10)) return 2;
    long spans=argc>3?number(argv[3],1,MaxSpans):MaxSpans, timeout=argc>4?number(argv[4],100,30000):30000;
    long outputLimit=argc>5?number(argv[5],1024,MaxOutput):MaxOutput;
    if(spans<0 || timeout<0 || outputLimit<0) return 2;
    std::vector<Selection> selections;
    if(argc==10) {
        if(wcscmp(argv[6],L"--select-video-pts-ns") || wcscmp(argv[8],L"--tolerance-ns") || wcslen(argv[7])>1408)return 2;
        auto exact=[](const std::wstring& value,uint64_t& n) {n=0;if(value.empty())return false;for(auto c:value){if(c<L'0'||c>L'9'||n>(MaxTime-(c-L'0'))/10)return false;n=n*10+c-L'0';}return true;};
        uint64_t tolerance;if(!exact(argv[9],tolerance) || tolerance>1000000)return 2;
        std::wstring list=argv[7];size_t start=0;
        while(start<=list.size()) {
            auto end=list.find(L',',start);if(end==std::wstring::npos)end=list.size();uint64_t target;
            if(selections.size()==64 || !exact(list.substr(start,end-start),target))return 2;
            for(const auto& q:selections)if(q.target==target)return 2;
            Selection q;q.target=target;q.tolerance=tolerance;selections.push_back(q);
            if(end==list.size())break;start=end+1;
        }
    }
    auto local=[](const wchar_t* path) { std::filesystem::path p(path); auto s=p.native(); return p.is_absolute() && s.size()>3 && s[1]==L':' &&
        (GetDriveTypeW(p.root_path().c_str())==DRIVE_FIXED || GetDriveTypeW(p.root_path().c_str())==DRIVE_RAMDISK); };
    if(!local(argv[1]) || !local(argv[2])) return 2;
    HANDLE input=CreateFileW(argv[1],GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(input==INVALID_HANDLE_VALUE) return 2;
    HANDLE output=CreateFileW(argv[2],GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(output==INVALID_HANDLE_VALUE) { CloseHandle(input); return 2; }
    Probe p; p.budget.maxSpans=spans;p.selections=selections;
    bool eos=false; std::string sha,error; LARGE_INTEGER size{};
    const auto deadline=std::chrono::steady_clock::now()+std::chrono::milliseconds(timeout);
    GstBus* bus=nullptr;
    try {
        if(!GetFileSizeEx(input,&size) || size.QuadPart<0 || static_cast<guint64>(size.QuadPart)>MaxFile) p.budget.fail("input_limit");
        else {
            GChecksum* sum=g_checksum_new(G_CHECKSUM_SHA256); std::array<guchar,65536> bytes{}; DWORD count=0; BOOL read;
            while((read=ReadFile(input,bytes.data(),static_cast<DWORD>(bytes.size()),&count,nullptr)) && count) {
                g_checksum_update(sum,bytes.data(),count);
                if(std::chrono::steady_clock::now()>=deadline) { p.budget.fail("deadline"); break; }
            }
            if(!read)p.budget.fail("hash_read_failed");
            if(count) p.budget.fail("hash_incomplete");
            if(!p.budget.stopped) sha=g_checksum_get_string(sum);
            g_checksum_free(sum);
        }
        if(!p.budget.stopped) {
            gst_init(nullptr,nullptr); gst_debug_set_default_threshold(GST_LEVEL_NONE);
            GError* parseError=nullptr;
            p.pipeline=gst_parse_launch("filesrc name=file ! matroskademux name=demux",&parseError);
            if(parseError || !p.pipeline) { if(parseError)g_error_free(parseError); error="pipeline_unavailable"; }
            else {
                auto* file=gst_bin_get_by_name(GST_BIN(p.pipeline),"file");
                const auto path=std::filesystem::path(argv[1]).u8string(); g_object_set(file,"location",path.c_str(),nullptr);gst_object_unref(file);
                auto* demux=gst_bin_get_by_name(GST_BIN(p.pipeline),"demux");
                g_signal_connect(demux,"pad-added",G_CALLBACK(pad_added),&p);gst_object_unref(demux);
                bus=gst_element_get_bus(p.pipeline);
                if(std::chrono::steady_clock::now()>=deadline) p.budget.fail("deadline");
                else if(gst_element_set_state(p.pipeline,GST_STATE_PLAYING)==GST_STATE_CHANGE_FAILURE) error="state_failed";
                else while(true) {
                    GstMessage* message=gst_bus_timed_pop_filtered(bus,10*GST_MSECOND,static_cast<GstMessageType>(GST_MESSAGE_EOS|GST_MESSAGE_ERROR));
                    if(message) { eos=GST_MESSAGE_TYPE(message)==GST_MESSAGE_EOS; if(!eos)error="gstreamer_error";gst_message_unref(message);break; }
                    { std::lock_guard<std::mutex> lock(p.mutex);
                      if(std::chrono::steady_clock::now()>=deadline) p.budget.fail("deadline");
                      if(p.budget.stopped)break; }
                }
            }
        }
    } catch(...) { error="probe_failed"; }
    if(p.pipeline)gst_element_set_state(p.pipeline,GST_STATE_NULL);
    if(bus)gst_object_unref(bus);
    if(p.pipeline)gst_object_unref(p.pipeline);
    if(!p.video.decoded.buffers && !p.audio.decoded.buffers && error.empty() && p.budget.reason.empty()) error="no_decoded_buffers";
    for(const auto* t:{&p.video,&p.audio}) {
        if(!t->padName.empty() && !t->decoded.buffers && error.empty() && p.budget.reason.empty()) error="track_without_decoded_buffers";
    }
    auto json=result_json(p,sha,static_cast<guint64>(size.QuadPart),eos,error,outputLimit);
    DWORD written=0; bool saved=WriteFile(output,json.data(),static_cast<DWORD>(json.size()),&written,nullptr) && written==json.size();
    CloseHandle(output);CloseHandle(input);
    if(!saved)return 5;
    return eos && error.empty() && p.budget.reason.empty() && json.find("\"reason\":\"output_limit\"")==std::string::npos ? 0 : 3;
}
#endif
