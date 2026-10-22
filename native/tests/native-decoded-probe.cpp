#define SBREC_DECODED_PROBE_TEST
#include "../native-decoded-probe.cpp"
#include "../native-synthetic-media.h"
#include <cstdio>
using namespace decoded_probe;
static int checks=0;
#define CHECK(x) do { ++checks; if(!(x)) { printf("FAIL line=%d expression=%s\n",__LINE__,#x); return 1; } } while(0)
int unit() {
    gst_init(nullptr,nullptr);
    Budget b; Stage s(&b); GstSegment segment; gst_segment_init(&segment,GST_FORMAT_TIME); s.segment(segment,1);
    for(int i=0;i<3;i++)s.buffer(i*10,10,0,1);
    CHECK(s.spans.size()==1 && s.spans[0].endPts==30);
    s.buffer(40,10,0,1); CHECK(s.spans.size()==2 && s.spans.back().reason=="timestamp_hole" && s.spans.back().previousEndPts==30);
    s.buffer(5,10,0,1); CHECK(s.spans.size()==3 && s.spans.back().reason=="timestamp_regression");
    segment.start=1000; segment.base=100; s.segment(segment,2); s.buffer(1000,10,0,1);
    CHECK(s.segments.size()==2 && s.spans.back().firstRunning==100 && s.spans.back().segment==2);
    s.buffer(1010,10,GST_BUFFER_FLAG_DISCONT,1); CHECK(s.spans.back().reason=="discont" && s.transitions.size()==1);
    s.buffer(1020,decoded_probe::Unknown,0,1); CHECK(s.spans.back().lastDuration==decoded_probe::Unknown && s.spans.back().endPts==decoded_probe::Unknown);
    s.buffer(1040,10,0,1); CHECK(s.spans.back().reason=="unknown_previous_duration_or_time");
    s.event("gap_event",1050,20); s.buffer(1070,10,0,1); CHECK(s.spans.back().reason=="gap_event" && s.transitions.back().duration==20);
    Budget limit;limit.maxSpans=1;Stage limited(&limit);gst_segment_init(&segment,GST_FORMAT_TIME);limited.segment(segment,1);
    limited.buffer(0,10,0,1);limited.buffer(20,10,0,1);CHECK(limit.reason=="span_limit" && limited.spans.size()==1);
    Budget expanded;Stage separate(&expanded);separate.segment(segment,1);
    for(uint32_t i=0;i<MaxSpans;i++)separate.buffer(i*20,10,0,1);
    CHECK(expanded.maxSpans==4096 && separate.spans.size()==4096 && !expanded.stopped);
    separate.buffer(MaxSpans*20,10,0,1);CHECK(expanded.reason=="span_limit" && separate.spans.size()==4096);
    Budget overflow;Stage huge(&overflow);huge.segment(segment,1);huge.buffer(MaxTime-5,10,0,1);CHECK(overflow.reason=="timestamp_overflow" && huge.spans.empty());
    Budget segmentOverflow;Stage invalidSegment(&segmentOverflow);segment.start=MaxTime+1;invalidSegment.segment(segment,1);CHECK(segmentOverflow.reason=="segment_timestamp_overflow");gst_segment_init(&segment,GST_FORMAT_TIME);
    Budget rate;Stage alteredRate(&rate);segment.rate=0.0;alteredRate.segment(segment,1);CHECK(rate.reason=="unsupported_segment_rate");segment.rate=1.0;
    Budget flags;Stage flagged(&flags);flagged.segment(segment,1);flagged.buffer(0,10,GST_BUFFER_FLAG_GAP,1);CHECK(flagged.transitions[0].kind==std::string("gap_buffer"));flagged.buffer(10,10,GST_BUFFER_FLAG_CORRUPTED,1);CHECK(flags.reason=="corrupted_buffer");
    Budget transitions;Stage many(&transitions);for(int i=0;i<4097;i++)many.event("discont");CHECK(transitions.reason=="transition_limit" && many.transitions.size()==4096);
    Budget offset;Stage rebased(&offset);segment.start=3600000000000000ULL;rebased.segment(segment,1);
    rebased.buffer(segment.start+123000000,10000000,0,1);CHECK(rebased.spans[0].firstRunning==123000000);
    CHECK(integer(3600000000000000ULL)=="\"3600000000000000\"" && integer(decoded_probe::Unknown)=="null");

    // Real sticky-event path, compared with B0's first-segment-only observer.
    GstPad* pad=gst_pad_new("test",GST_PAD_SRC);gst_pad_set_active(pad,TRUE);
    GstCaps* caps=gst_caps_from_string("video/x-raw,format=NV12,width=64,height=64,framerate=10/1");
    gst_pad_store_sticky_event(pad,gst_event_new_caps(caps));gst_caps_unref(caps);
    gst_segment_init(&segment,GST_FORMAT_TIME);GstEvent* event=gst_event_new_segment(&segment);gst_event_set_seqnum(event,11);gst_pad_store_sticky_event(pad,event);gst_event_unref(event);
    GstBuffer* buffer=gst_buffer_new();GST_BUFFER_PTS(buffer)=0;GST_BUFFER_DURATION(buffer)=10;
    Budget sticky;Stage sampled(&sticky);Samples legacy{"legacy"};
    GstPadProbeInfo info{};info.type=GST_PAD_PROBE_TYPE_BUFFER;info.data=buffer;
    sampled.pad_buffer(pad,buffer);observe(pad,&info,&legacy);
    segment.start=1000;segment.base=500;event=gst_event_new_segment(&segment);gst_event_set_seqnum(event,12);gst_pad_store_sticky_event(pad,event);gst_event_unref(event);
    GST_BUFFER_PTS(buffer)=1000;sampled.pad_buffer(pad,buffer);observe(pad,&info,&legacy);
    CHECK(sampled.segments.size()==2 && sampled.spans.back().firstRunning==500);
    CHECK(legacy.segment.base==0 && legacy.lastRunning==1000); // negative control: old summary misses change
    gst_buffer_unref(buffer);gst_pad_set_active(pad,FALSE);gst_object_unref(pad);
    Probe p;p.video.padName="video_0";p.audio.padName="audio_0";gst_segment_init(&segment,GST_FORMAT_TIME);
    p.video.decoded.segment(segment,1);p.audio.decoded.segment(segment,1);
    p.video.decoded.buffer(0,10000000,0,1);p.audio.decoded.buffer(7000000,decoded_probe::Unknown,0,1);
    CHECK(p.video.decoded.spans[0].firstPts==0 && p.audio.decoded.spans[0].firstPts==7000000);
    std::string json=result_json(p,"fixture",100,true,"",MaxOutput);
    CHECK(json.find("\"sourceTailKnown\":false")!=std::string::npos && json.find("\"available\":false")!=std::string::npos);
    CHECK(result_json(p,"fixture",100,true,"",1024).find("\"reason\":\"output_limit\"")!=std::string::npos);
    Probe selection;selection.video.padName="video_0";selection.selections.push_back({1792000000000000000ULL,1000000});
    selection.video.decoded.segment(segment,1);selection.video.decoded.eos=true;
    selection.video.decoded.buffer(1792000000000000000ULL,33333333,0,1);
    CHECK(result_json(selection,"fixture",100,true,"",MaxOutput).find("unique_observed")!=std::string::npos);
    selection.video.decoded.buffer(1792000000000500000ULL,33333333,0,1);
    CHECK(result_json(selection,"fixture",100,true,"",MaxOutput).find("ambiguous")!=std::string::npos);
    CHECK(result_json(selection,"fixture",100,false,"",MaxOutput).find("\"status\":\"incomplete\"")!=std::string::npos);
    printf("PASS unit_checks=%d later_segment_negative_control=1 integer_rebase_ns=3600000000000000\n",checks);return 0;
}
struct Fixture { Samples video{"encoded_video"}; bool gapped=false,keys=true;int dropped=0; };
GstPadProbeReturn fixture_video(GstPad* pad,GstPadProbeInfo* info,gpointer value) {
    auto& f=*static_cast<Fixture*>(value);observe(pad,info,&f.video);
    if(f.video.count==11 || f.video.count==21) f.keys=f.keys && !GST_BUFFER_FLAG_IS_SET(GST_PAD_PROBE_INFO_BUFFER(info),GST_BUFFER_FLAG_DELTA_UNIT);
    if(f.gapped && f.video.count>=11 && f.video.count<=20) { ++f.dropped;return GST_PAD_PROBE_DROP; }
    return GST_PAD_PROBE_OK;
}
int main(int argc,char** argv) {
    if(argc==2 && !strcmp(argv[1],"unit"))return unit();
    if(argc!=4 || strcmp(argv[1],"fixture"))return 2;
    const auto path=std::filesystem::u8path(argv[2]);
    if(!path.is_absolute())return 2;
    HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return 2;CloseHandle(file);
    gst_init(nullptr,nullptr);Fixture f;f.gapped=!strcmp(argv[3],"gapped");
    const auto description="matroskamux name=mux ! filesink name=file "+synthetic_media(3,640,360,10,"mux.",false);
    GError* error=nullptr;auto* root=gst_parse_launch(description.c_str(),&error);if(error || !root)return 3;
    auto* sink=gst_bin_get_by_name(GST_BIN(root),"file");g_object_set(sink,"location",argv[2],nullptr);gst_object_unref(sink);
    auto* video=gst_bin_get_by_name(GST_BIN(root),"video");auto* pad=gst_element_get_static_pad(video,"src");gst_pad_add_probe(pad,GST_PAD_PROBE_TYPE_BUFFER,fixture_video,&f,nullptr);gst_object_unref(pad);gst_object_unref(video);
    auto* bus=gst_element_get_bus(root);int result=3;
    if(gst_element_set_state(root,GST_STATE_PLAYING)!=GST_STATE_CHANGE_FAILURE) {
        auto* message=gst_bus_timed_pop_filtered(bus,20*GST_SECOND,static_cast<GstMessageType>(GST_MESSAGE_ERROR|GST_MESSAGE_EOS));
        if(message) { if(GST_MESSAGE_TYPE(message)==GST_MESSAGE_EOS)result=0;gst_message_unref(message); }
    }
    gst_element_set_state(root,GST_STATE_NULL);gst_object_unref(bus);gst_object_unref(root);
    printf("fixture_video_buffers=%llu encoded_first_pts_ns=%llu encoded_first_running_ns=%llu dropped_gop_buffers=%d key_boundaries=%d\n",f.video.count,f.video.first,f.video.firstRunning,f.dropped,f.keys);
    if(!f.keys || f.video.first<3600000000000000ULL || (f.gapped && f.dropped!=10))return 3;
    return result;
}
