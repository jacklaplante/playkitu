#ifdef SBREC_CLOCK_SOURCE_RUNNER
#define SBREC_RUNNER_CLOCK_TEST
#include "../native-synthetic-runner.cpp"
static bool delayed=false;
static uint64_t delayBefore=0,delayAfter=0;
static FrameAnchorV2 delayReference{};
void sbrec_test_source_delay(uint64_t ordinal,GstPad* pad,GstBuffer* buffer) noexcept {
    if(delayed && ordinal==31){
        auto segment=media_clock::segment_at(pad,GST_BUFFER_PTS(buffer));auto* source=gst_pad_get_parent_element(pad);
        auto sample=media_clock::sample_source(source);delayReference=sample.data;
        delayReference.sourceRunningNs=segment.running;delayReference.sourcePtsNs=GST_BUFFER_PTS(buffer);
        if(sample.identity)gst_object_unref(sample.identity);gst_object_unref(source);
        LARGE_INTEGER q;QueryPerformanceCounter(&q);delayBefore=q.QuadPart;Sleep(250);QueryPerformanceCounter(&q);delayAfter=q.QuadPart;
    }
}
#else
#include "../native-synthetic-runner.h"
#include <windows.h>
#endif
#include "../native-media-clock.h"
#include <cstdio>
#include <cstring>
#include <fstream>
#include <filesystem>
#include <thread>
#include <chrono>
#include <vector>
#include <memory>
static int checks=0;
#define CHECK(x) do { ++checks; if(!(x)) { printf("FAIL line=%d expression=%s\n",__LINE__,#x); return 1; } } while(0)

const char* fields[]={"sequence","frameOrdinal","clockEpoch","producerEpoch","qpcBefore","qpcAfter","qpcFrequency",
    "gstClockTimeNs","pipelineBaseTimeNs","clockResolutionNs","calibrationInternalNs","calibrationExternalNs","calibrationRateNumerator","calibrationRateDenominator",
    "padArrivalQpc","estimatedScheduleQpc","scheduleUncertaintyNs","sourcePtsNs","sourceDurationNs","sourceRunningNs","sourceSegmentStartNs","sourceSegmentBaseNs",
    "encodedPtsNs","encodedRunningNs","encodedSegmentStartNs","encodedSegmentBaseNs","sdkInputRunningNs","sdkPts100ns"};
void row_json(std::ostream& o,const FrameAnchorV2& r) {
    o<<"{\"size\":"<<r.size<<",\"version\":"<<r.version<<",\"kind\":"<<r.kind<<",\"flags\":"<<r.flags<<",\"track\":"<<r.track<<",\"reason\":"<<r.reason
     <<",\"sourceSegmentSeq\":"<<r.sourceSegmentSeq<<",\"encodedSegmentSeq\":"<<r.encodedSegmentSeq;
    for(size_t i=0;i<28;i++){uint64_t n;memcpy(&n,reinterpret_cast<const char*>(&r)+32+i*8,8);o<<",\""<<fields[i]<<"\":";if(n==UINT64_MAX)o<<"null";else o<<'"'<<n<<'"';}o<<'}';
}
int unit() {
    using media_clock::Engine;using media_clock::Sample;using media_clock::SegmentSample;
    gst_init(nullptr,nullptr);GstClock* clock=gst_system_clock_obtain();
    auto sample=[&](uint64_t c,uint64_t q){Sample s;s.identity=clock;s.supported=true;s.data=media_clock::empty_record(CLOCK_SUPPORT);
        s.data.qpcBefore=q;s.data.qpcAfter=q+2;s.data.qpcFrequency=1000000000;s.data.gstClockTimeNs=c;s.data.pipelineBaseTimeNs=1000;s.data.clockResolutionNs=1;
        s.data.calibrationInternalNs=s.data.calibrationExternalNs=0;s.data.calibrationRateNumerator=s.data.calibrationRateDenominator=1;return s;};
    SegmentSample source,encoded;gst_segment_init(&source.segment,GST_FORMAT_TIME);source.sequence=1;source.valid=true;source.running=20;
    encoded=source;encoded.sequence=2;encoded.segment.start=3600000000000000ULL;
    auto fill=[&](Engine& value,int duplicate=0){value.source_segment(source);value.clock_sample(sample(1010,2000));value.clock_sample(sample(1030,2020));auto late=sample(1300,2290);value.clock_sample(late);
        value.source(31,20,10,source,2291,late,0,0);if(duplicate==2)value.source_segment(source);
        value.encoded(3600000000000020ULL,encoded,0);if(duplicate==3)value.encoded(3600000000000020ULL,encoded,0);
        value.submitted(20,17920000000000000ULL,1,0,0);
        if(duplicate==1)value.submitted(20,17920000000000000ULL,1,0,0);
        auto advance=encoded;advance.running=30;value.encoded(3600000000000030ULL,advance,0);value.submitted(30,17920000000000001ULL,0,0,0);
        auto nextSource=source;nextSource.running=30;value.source_segment(nextSource);};
    FrameAnchorV2 rows[64];auto e=std::make_unique<Engine>(64,1000);fill(*e);auto n=e->drain(rows,64);
    CHECK(n==3 && rows[0].kind==CLOCK_SUPPORT && rows[2].kind==CLOCK_FRAME);
    CHECK(rows[2].flags==31 && rows[2].reason==0 && rows[2].estimatedScheduleQpc==2011 && rows[2].scheduleUncertaintyNs==12);
    CHECK(rows[2].padArrivalQpc-rows[2].estimatedScheduleQpc==280);
    CHECK(rows[2].encodedPtsNs==3600000000000020ULL && rows[2].sdkPts100ns==17920000000000000ULL);
    auto duplicate=std::make_unique<Engine>(64,1000);fill(*duplicate,true);n=duplicate->drain(rows,64);CHECK(n==1 && rows[0].reason==CLOCK_AMBIGUOUS && rows[0].estimatedScheduleQpc==UINT64_MAX);
    for(int duplicateStage:{2,3}){auto duplicated=std::make_unique<Engine>(64,1000);fill(*duplicated,duplicateStage);n=duplicated->drain(rows,64);CHECK(n==1 && rows[0].reason==CLOCK_AMBIGUOUS && !(rows[0].flags&CLOCK_ESTIMATE));}
    auto bound=std::make_unique<Engine>(1,1000);fill(*bound);n=bound->drain(rows,64);CHECK(n==1 && rows[0].reason==CLOCK_OUTPUT_LOSS && !(rows[0].flags&CLOCK_ESTIMATE) && bound->dropped()==2);
    auto reset=std::make_unique<Engine>(64,1000);reset->source_segment(source);reset->clock_sample(sample(1010,2000));auto changed=sample(1020,2010);changed.data.pipelineBaseTimeNs=2000;reset->clock_sample(changed);
    n=reset->drain(rows,64);CHECK(n==1 && rows[0].kind==CLOCK_BOUNDARY && rows[0].reason==CLOCK_CHANGED && rows[0].clockEpoch==2);
    auto segments=std::make_unique<Engine>(64,1000);segments->source_segment(source);auto another=source;another.sequence=3;segments->source_segment(another);n=segments->drain(rows,64);CHECK(n==1 && rows[0].reason==CLOCK_SEGMENT_CHANGED);
    auto unknown=std::make_unique<Engine>(64,1000);unknown->source_segment(source);auto missing=sample(1010,2000);missing.supported=false;unknown->source(1,20,10,source,2001,missing,0,0);unknown->finish();n=unknown->drain(rows,64);CHECK(n==1 && rows[0].reason==CLOCK_UNSUPPORTED && !(rows[0].flags&CLOCK_ESTIMATE));
    auto overflow=std::make_unique<Engine>(64,1000);overflow->source_segment(source);auto huge=sample(1010,2000);huge.data.pipelineBaseTimeNs=G_MAXINT64;overflow->source(1,20,10,source,2001,huge,0,0);overflow->finish();n=overflow->drain(rows,64);CHECK(n==1 && rows[0].reason==CLOCK_ARITHMETIC);
    auto noBracket=std::make_unique<Engine>(64,1000);noBracket->source_segment(source);auto late=sample(1300,2290);noBracket->clock_sample(late);noBracket->source(1,20,10,source,2291,late,0,0);noBracket->finish();n=noBracket->drain(rows,64);CHECK(n==1 && rows[0].reason==CLOCK_NO_BRACKET);
    auto stale=std::make_unique<Engine>(64,1000);stale->source_segment(source);stale->clock_sample(late);
    auto newer=sample(1010,3000);newer.data.pipelineBaseTimeNs=2000;stale->clock_sample(newer);
    newer=sample(1030,3020);newer.data.pipelineBaseTimeNs=2000;stale->clock_sample(newer);
    CHECK(!stale->clock_sample(late));stale->source(1,20,10,source,2291,late,0,0);stale->finish();n=stale->drain(rows,64);
    CHECK(n>=2 && rows[n-1].reason==CLOCK_CHANGED && !(rows[n-1].flags&CLOCK_ESTIMATE));
    stale->boundary(CLOCK_PRODUCER_RESET);CHECK(!stale->clock_sample(late));
    auto loss=std::make_unique<Engine>(64,1000);loss->source_segment(source);loss->clock_sample(sample(1010,2000));loss->clock_sample(sample(1030,2020));loss->source(1,20,10,source,2021,sample(1030,2020),0,1);loss->finish();n=loss->drain(rows,64);CHECK(n==2 && rows[1].reason==CLOCK_OBSERVATION_LOSS);
    auto pending=std::make_unique<Engine>(64,1000);pending->source_segment(source);for(int i=0;i<17;i++)pending->source(i+1,20,10,source,2001,sample(1010,2000),0,0);n=pending->drain(rows,64);CHECK(n==1 && rows[0].reason==CLOCK_PENDING_LIMIT);
    auto tail=std::make_unique<Engine>(64,1000);tail->source_segment(source);tail->clock_sample(sample(1010,2000));tail->clock_sample(sample(1030,2020));tail->source(1,20,10,source,2021,sample(1030,2020),0,0);tail->finish();n=tail->drain(rows,64);CHECK(n==1 && rows[0].reason==CLOCK_UNMATCHED_END);
    CHECK(sizeof(SbrecObservation)==264 && sizeof(FrameAnchorV2)==256 && offsetof(FrameAnchorV2,qpcBefore)==64);
    gst_object_unref(clock);printf("PASS clock_unit_checks=%d\n",checks);return 0;
}
int main(int argc,char** argv) {
    if(argc==2 && !strcmp(argv[1],"unit"))return unit();
    if(argc!=7)return 2;
    std::string mode=argv[5];
#ifdef SBREC_CLOCK_SOURCE_RUNNER
    delayed=mode=="delay";
#else
    if(mode=="delay")return 2;
#endif
    HMODULE plugin=LoadLibraryA(argv[1]);CHECK(plugin);
    auto configure=reinterpret_cast<uint32_t(__cdecl*)(uint32_t,const char*)>(GetProcAddress(plugin,"sbrec_test_configure"));
    CHECK(configure && configure(1,argv[2])==0); // never transition a production plugin
    uint32_t version=0,bytes=0;CHECK(sbrec_runner_abi_version()==1);CHECK(sbrec_runner_clock_v2_info(&version,&bytes)==0 && version==2 && bytes==256);
    uint64_t handle;CHECK(sbrec_runner_create(&handle)==0);
    FrameAnchorV2 output[64];memset(output,0xa5,sizeof(output));unsigned char untouched[sizeof(output)];memcpy(untouched,output,sizeof(output));uint32_t count=99;uint64_t dropped=0;
    CHECK(sbrec_runner_drain_clock_v2(handle,output,256,64,&count,&dropped)==RUN_NOT_READY && !count);
    CHECK(sbrec_runner_enable_clock_v2(handle,1,256,64,1000)==RUN_INVALID);
    CHECK(sbrec_runner_enable_clock_v2(handle,2,264,64,1000)==RUN_INVALID);
    CHECK(sbrec_runner_enable_clock_v2(handle,2,256,mode=="overflow"?1:64,1000)==0);
    CHECK(sbrec_runner_drain_clock_v2(handle,output,264,64,&count,&dropped)==RUN_INVALID && !count && !memcmp(untouched,output,sizeof(output)));
    LARGE_INTEGER callerBefore,callerAfter;QueryPerformanceCounter(&callerBefore);
    SbrecClock native{};sbrec_runner_clock(&native);QueryPerformanceCounter(&callerAfter);
    CHECK(static_cast<uint64_t>(callerBefore.QuadPart)<=native.qpc && native.qpc<=static_cast<uint64_t>(callerAfter.QuadPart));
    SbrecRunSettings s{};s.size=sizeof(s);s.seconds=4;s.width=640;s.height=360;s.fps=30;s.observationCapacity=mode=="overflow"?1:1024;s.startupMs=9000;s.flushMs=2000;s.encodedByteLimit=64ULL*1024*1024;
    strcpy_s(s.sessionId,"00000000-0000-4000-8000-000000000101");strcpy_s(s.runId,argv[6]);strcpy_s(s.streamName,"sbrec-offline");strcpy_s(s.caFile,argv[3]);
    s.managedTicks=s.correspondenceQpc=native.qpc;s.managedFrequency=native.frequency;s.correspondenceUncertainty100ns=native.uncertainty100ns;
    const char access[]="OFFLINEONLYACCESS00000001",secret[]="OFFLINEONLYSECRET000000000000001",token[]="OFFLINEONLYTOKEN";
    SbrecCredentialUpdate c{access,sizeof(access)-1,secret,sizeof(secret)-1,token,sizeof(token)-1,native.unix100ns/10000000+600};
    CHECK(sbrec_runner_start(handle,&s,&c)==0);CHECK(sbrec_runner_enable_clock_v2(handle,2,256,64,1000)==RUN_BUSY);
    std::vector<FrameAnchorV2> records;SbrecRunStatus status{};status.size=sizeof(status);bool copiedV1=false,copiedV2=false;
    auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(25);
    while(std::chrono::steady_clock::now()<deadline) {
        CHECK(sbrec_runner_status(handle,&status)==0);
        if(mode!="overflow" || status.state==RUN_TERMINAL) {
            struct V1Canary {uint64_t before;SbrecObservation row;uint64_t after;} v1Canary{};
            v1Canary.before=v1Canary.after=0x1122334455667788ULL;uint32_t single=0;
            CHECK(sbrec_runner_drain_observations(handle,&v1Canary.row,1,&single)==0);
            CHECK(v1Canary.before==0x1122334455667788ULL && v1Canary.after==0x1122334455667788ULL);copiedV1=copiedV1 || single;
            struct V2Canary {uint64_t before;FrameAnchorV2 row;uint64_t after;} v2Canary{};
            v2Canary.before=v2Canary.after=0x8877665544332211ULL;
            CHECK(sbrec_runner_drain_clock_v2(handle,&v2Canary.row,256,1,&single,&dropped)==0);
            CHECK(v2Canary.before==0x8877665544332211ULL && v2Canary.after==0x8877665544332211ULL);copiedV2=copiedV2 || single;
            if(single)records.push_back(v2Canary.row);
            SbrecObservation v1[256];uint32_t n1=0;CHECK(sbrec_runner_drain_observations(handle,v1,256,&n1)==0);
            CHECK(sbrec_runner_drain_clock_v2(handle,output,256,64,&count,&dropped)==0);
            CHECK(records.size()+count<=256);records.insert(records.end(),output,output+count);
        }
        if(status.state==RUN_TERMINAL)break;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    CHECK(status.state==RUN_TERMINAL && status.workerDone && status.callbacksQuiescent);
    CHECK(copiedV1 && copiedV2);
    unsigned valid=0;bool delayedFound=false;
    for(const auto& r:records)if(r.kind==CLOCK_FRAME && r.reason==0 && (r.flags&15)==15) {
        ++valid;CHECK(r.sourceRunningNs==r.encodedRunningNs && r.encodedRunningNs==r.sdkInputRunningNs);
        CHECK(r.encodedPtsNs>=3600000000000000ULL && r.estimatedScheduleQpc!=UINT64_MAX && r.scheduleUncertaintyNs<100000000);
        unsigned support=0;uint64_t lo=UINT64_MAX,hi=0;
        for(const auto& x:records)if(x.kind==CLOCK_SUPPORT && x.frameOrdinal==r.frameOrdinal && x.clockEpoch==r.clockEpoch){++support;lo=(std::min)(lo,x.qpcBefore);hi=(std::max)(hi,x.qpcAfter);}
        CHECK(support==2 && lo<=r.estimatedScheduleQpc && r.estimatedScheduleQpc<=hi);
#ifdef SBREC_CLOCK_SOURCE_RUNNER
        if(mode=="delay" && r.frameOrdinal==31){delayedFound=true;auto uncertainty=gst_util_uint64_scale_ceil(r.scheduleUncertaintyNs,r.qpcFrequency,GST_SECOND);
            CHECK(r.sourceRunningNs==delayReference.sourceRunningNs && r.sourcePtsNs==delayReference.sourcePtsNs && r.pipelineBaseTimeNs==delayReference.pipelineBaseTimeNs);
            CHECK(gst_util_uint64_scale(delayAfter-delayBefore,GST_SECOND,r.qpcFrequency)>=200000000);
            CHECK(r.padArrivalQpc>r.estimatedScheduleQpc+uncertainty);
            printf("delayed_frame=%llu delay_ns=%llu arrival_minus_schedule_ns=%llu schedule_uncertainty_ns=%llu\n",r.frameOrdinal,
                gst_util_uint64_scale(delayAfter-delayBefore,GST_SECOND,r.qpcFrequency),gst_util_uint64_scale(r.padArrivalQpc-r.estimatedScheduleQpc,GST_SECOND,r.qpcFrequency),r.scheduleUncertaintyNs);}
#endif
    }
    if(mode=="overflow")CHECK(!valid && (status.droppedObservations || dropped));else CHECK(valid>=2 && !status.droppedObservations && !dropped);
    if(mode=="delay")CHECK(delayedFound);
    HANDLE reserve=CreateFileW(std::filesystem::u8path(argv[4]).c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);CHECK(reserve!=INVALID_HANDLE_VALUE);CloseHandle(reserve);
    std::ofstream out(std::filesystem::u8path(argv[4]));out<<"{\"schema\":2,\"sessionId\":\""<<s.sessionId<<"\",\"runId\":\""<<s.runId<<"\",\"mode\":\""<<mode
      <<"\",\"callerClock\":\"native_test_QPC_not_managed_Stopwatch\",\"callerBeforeQpc\":\""<<callerBefore.QuadPart<<"\",\"callerAfterQpc\":\""<<callerAfter.QuadPart
      <<"\",\"sessionAnchorQpc\":\""<<native.qpc<<"\",\"qpcFrequency\":\""<<native.frequency
      <<"\",\"clockDropped\":\""<<dropped<<"\",\"v1Dropped\":\""<<status.droppedObservations<<"\",\"anchors\":[";
    bool comma=false;for(const auto& r:records){if(comma)out<<',';comma=true;row_json(out,r);}out<<']';
#ifdef SBREC_CLOCK_SOURCE_RUNNER
    if(mode=="delay"){out<<",\"beforeDelayBuffer\":";row_json(out,delayReference);out<<",\"delayBeforeQpc\":\""<<delayBefore<<"\",\"delayAfterQpc\":\""<<delayAfter<<'"';}
#endif
    out<<"}\n";out.close();CHECK(out.good());
    CHECK(sbrec_runner_release(handle)==0);printf("PASS clock_integration_checks=%d valid=%u mode=%s abi1=264 abi2=256\n",checks,valid,argv[5]);return 0;
}
