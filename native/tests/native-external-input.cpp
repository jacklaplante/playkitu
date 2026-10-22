#define SBREC_RUNNER_EXPORT
#include "../native-synthetic-runner.h"
#include "../native-synthetic-media.h"
#include <gst/app/gstappsink.h>
#include <windows.h>
#include <chrono>
#include <thread>
#include <vector>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <atomic>
#include <string>
#include <fstream>
#include <sstream>
#include <filesystem>

static int checks=0;
static void startup_phase(const SbrecObservation& row,uint32_t& seen) {
    if(row.kind!=OBS_STARTUP||row.flags<1||row.flags>7)return;
    seen|=1u<<row.flags;
    printf("{\"stage\":\"native-startup\",\"phase\":%u,\"elapsedMs\":%u}\n",row.flags,row.result);
}
#define CHECK(x) do {++checks;if(!(x)){printf("FAIL line=%d expression=%s\n",__LINE__,#x);return 1;}}while(0)
using Steady=std::chrono::steady_clock;
struct Decoded {uint64_t video=0,audio=0,firstAudio=GST_CLOCK_TIME_NONE,lastAudioEnd=0,audioGaps=0,maxAudioGap=0;double topRed=0,topBlue=0,bottomRed=0,bottomBlue=0,energy=0,leftEnergy=0,rightEnergy=0;bool valid=true;};
// E41 test-only reader: actual exported managed PCM, without duplicating its
// conversion. File hashes are checked by the driver; validate all bounds again.
struct ConvertedChunk {uint64_t offset,frames,position,gap;};
struct ConvertedPcm {std::vector<float> pcm;std::vector<ConvertedChunk> chunks;uint64_t end=0;};
static bool read_converted(const char* csv,const char* pcm,ConvertedPcm& out) {
 try {
  auto size=std::filesystem::file_size(pcm);
  if(!size||size>8*1024*1024||size%8||std::filesystem::file_size(csv)>1024*1024)return false;
  out.pcm.resize(size/4);std::ifstream audio(pcm,std::ios::binary);if(!audio.read(reinterpret_cast<char*>(out.pcm.data()),size))return false;
  for(float sample:out.pcm)if(!std::isfinite(sample)||std::abs(sample)>1)return false;
  std::ifstream desc(csv);std::string line;if(!std::getline(desc,line))return false;if(!line.empty()&&line.back()=='\r')line.pop_back();
  if(line!="fileFrameOffset,producedFrames,absoluteOutputFrame,gapBeforeFrames,captureQpc,sourceFirstFrame,sourceFrames,appliedOutputPerInput")return false;
  uint64_t nextOffset=0,previousEnd=0;
  while(std::getline(desc,line)){
   if(line.size()>1024||out.chunks.size()>=1024)return false;
   std::stringstream fields(line);std::string part;std::vector<std::string> values;while(std::getline(fields,part,','))values.push_back(part);
   if(values.size()!=8)return false;uint64_t n[7]{};
   for(int i=0;i<7;i++){if(values[i].empty()||values[i].find_first_not_of("0123456789")!=std::string::npos)return false;n[i]=std::stoull(values[i]);}
   size_t used=0;double ratio=std::stod(values[7],&used);if(used<values[7].size()&&values[7][used]!='\r')return false;
   if(!std::isfinite(ratio)||ratio<.9997||ratio>1.0003||n[0]!=nextOffset||!n[1]||n[1]>4800||n[2]<previousEnd||n[2]>240000||n[3]!=n[2]-previousEnd||n[4]!=1000000000ULL+n[2]*10000000ULL/48000||n[5]>240000||!n[6]||n[6]>4800)return false;
   nextOffset+=n[1];previousEnd=n[2]+n[1];if(nextOffset>size/8||previousEnd>240000)return false;
   out.chunks.push_back({n[0],n[1],n[2],n[3]});
  }
  out.end=previousEnd;return !out.chunks.empty()&&nextOffset==size/8;
 }catch(...){return false;}
}
static void fill_pixels(std::vector<uint8_t>& pixels,uint32_t width=320,uint32_t height=180) {
    for(uint32_t y=0;y<height;y++)for(uint32_t x=0;x<width;x++){auto* p=pixels.data()+y*(width*4+16)+x*4;p[0]=y<height/2?220:20;p[1]=24;p[2]=y<height/2?20:220;p[3]=255;}
}
static GstFlowReturn video_sample(GstAppSink* sink,gpointer opaque) {
    auto& d=*static_cast<Decoded*>(opaque);auto* sample=gst_app_sink_pull_sample(sink);if(!sample)return GST_FLOW_ERROR;
    auto* buffer=gst_sample_get_buffer(sample);GstMapInfo map{};
    if(!gst_buffer_map(buffer,&map,GST_MAP_READ)||map.size<320*180*4)d.valid=false;
    else {auto top=map.data+320*4*10+160*4;auto bottom=map.data+320*4*170+160*4;
        d.topRed+=top[0];d.topBlue+=top[2];d.bottomRed+=bottom[0];d.bottomBlue+=bottom[2];++d.video;gst_buffer_unmap(buffer,&map);}
    gst_sample_unref(sample);return GST_FLOW_OK;
}
static GstFlowReturn audio_sample(GstAppSink* sink,gpointer opaque) {
    auto& d=*static_cast<Decoded*>(opaque);auto* sample=gst_app_sink_pull_sample(sink);if(!sample)return GST_FLOW_ERROR;
    auto* buffer=gst_sample_get_buffer(sample);GstMapInfo map{};
    if(!gst_buffer_map(buffer,&map,GST_MAP_READ)||map.size%sizeof(float))d.valid=false;
    else {auto* values=reinterpret_cast<const float*>(map.data);for(size_t i=0;i<map.size/sizeof(float);i++){d.valid=d.valid&&std::isfinite(values[i]);d.energy+=values[i]*values[i];if(i%2)d.rightEnergy+=values[i]*values[i];else d.leftEnergy+=values[i]*values[i];++d.audio;}
        auto pts=GST_BUFFER_PTS(buffer);auto duration=(map.size/(sizeof(float)*2))*GST_SECOND/48000;
        if(pts!=GST_CLOCK_TIME_NONE){if(d.firstAudio==GST_CLOCK_TIME_NONE)d.firstAudio=pts;if(d.lastAudioEnd&&pts>d.lastAudioEnd+1000000){auto gap=pts-d.lastAudioEnd;d.audioGaps++;d.maxAudioGap=std::max(d.maxAudioGap,gap);printf("{\"stage\":\"decoded-audio-gap\",\"fromNs\":%llu,\"toNs\":%llu,\"gapNs\":%llu}\n",d.lastAudioEnd,pts,gap);}d.lastAudioEnd=pts+duration;}
        gst_buffer_unmap(buffer,&map);}
    gst_sample_unref(sample);return GST_FLOW_OK;
}
// Explicit loading reproduces the managed exact-module owner. The old harness
// import-linked the adapter, so FreeLibrary could not exercise actual DLL unload.
#define RUNNER_APIS(X) X(abi_version) X(clock) X(create) X(start) X(status) X(release) X(request_stop) X(enable_external_v1) X(external_v1_info) X(enable_clock_v2) X(external_status_v1) X(push_external_v1) X(drain_external_v1) X(drain_observations)
struct RunnerApi {
    HMODULE module=nullptr;
#define DECLARE(name) decltype(&::sbrec_runner_##name) name=nullptr;
    RUNNER_APIS(DECLARE)
#undef DECLARE
    bool load() {
        wchar_t path[32768];if(!GetModuleFileNameW(nullptr,path,32768))return false;
        std::wstring file(path);file=file.substr(0,file.find_last_of(L"\\/"))+L"/PlaytestRecordingNativeProof.dll";
        module=LoadLibraryExW(file.c_str(),nullptr,0x1100);if(!module)return false;
#define BIND(name) name=reinterpret_cast<decltype(name)>(GetProcAddress(module,"sbrec_runner_" #name));if(!name)return false;
        RUNNER_APIS(BIND)
#undef BIND
        return true;
    }
};
#undef RUNNER_APIS

int main(int argc,char** argv) {
    RunnerApi api;CHECK(api.load());
    setvbuf(stdout,nullptr,_IONBF,0);
    if(argc!=5&&argc!=7)return 2;std::string mode=argv[4];const bool converted=mode=="converted";
    ConvertedPcm convertedPcm;if(converted){CHECK(argc==7&&read_converted(argv[5],argv[6],convertedPcm));}else CHECK(argc==5);
    const bool controlTimeout=mode=="configured-control-timeout";
    const bool controlCancel=mode=="configured-control-cancel";
    const bool controlLate=mode=="configured-control-late";
    const bool lateSuccess=mode=="configured-control-late-success";
    const bool controlDelay=controlTimeout||controlCancel||controlLate;
    const bool configured=mode=="configured"||mode=="configured-no-input"||controlDelay||lateSuccess;
    const bool noInput=mode=="no-input"||mode=="configured-no-input"||controlDelay;
    HMODULE plugin=LoadLibraryA(argv[1]);CHECK(plugin);
    auto configure=reinterpret_cast<uint32_t(__cdecl*)(uint32_t,const char*)>(GetProcAddress(plugin,"sbrec_test_configure"));
    // D19 uses the EXISTING mode3 control-response hold. Maintained SDK request
    // setup/signing/cancellation executes normally; no primitive reaches sockets.
    // E41 needs one immutable complete local upload. Existing mode8 omits fake
    // ACKs and refuses a second writer instead of overwriting a prior MKV.
    CHECK(configure);CHECK(configure((controlTimeout||controlCancel)?3:(configured||converted)?8:1,argv[2])==0);
    if(controlLate||lateSuccess){
        auto delay=reinterpret_cast<uint32_t(__cdecl*)(uint32_t)>(GetProcAddress(plugin,"sbrec_test_setup_delay"));
        CHECK(delay&&delay(7000)==RUN_OK);
    }
    using Counts=uint32_t(__cdecl*)(uint32_t*,uint32_t*,uint32_t*,uint64_t*);
    auto counts=reinterpret_cast<Counts>(GetProcAddress(plugin,"sbrec_test_counts"));CHECK(counts);
    using Lifecycle=uint32_t(__cdecl*)(uint32_t,uint32_t*);
    auto lifecycle=reinterpret_cast<Lifecycle>(GetProcAddress(plugin,"sbrec_test_lifecycle_control"));CHECK(lifecycle);
    struct GateGuard {Lifecycle control;~GateGuard(){uint32_t state[6];control(2,state);}} gate{lifecycle};
    uint32_t life[6]{};if(mode=="retirement")CHECK(lifecycle(1,life)==0);
    if(mode=="retirement-refused")CHECK(lifecycle(3,life)==0);
 // refuse production kvssink before any pipeline state
    uint32_t version=0,inputBytes=0,statusBytes=0,eventBytes=0;
    CHECK(api.abi_version()==1);CHECK(api.external_v1_info(&version,&inputBytes,&statusBytes,&eventBytes)==0);
    CHECK(version==1&&inputBytes==72&&statusBytes==88&&eventBytes==72);
    SbrecRunSettings settings{};settings.size=sizeof(settings);settings.seconds=14400;settings.width=320;settings.height=180;settings.fps=30;
    if(configured){settings.width=1280;settings.height=720;}
    settings.startupMs=configured?10000:mode=="no-input" ? 1500 : 9000;settings.flushMs=2000;settings.observationCapacity=1024;settings.encodedByteLimit=64ULL*1024*1024;
    strcpy_s(settings.sessionId,"00000000-0000-4000-8000-000000000001");strcpy_s(settings.runId,"00000000-0000-4000-8000-000000000002");
    strcpy_s(settings.streamName,"external-offline");strcpy_s(settings.caFile,argv[3]);
    SbrecClock clock{};CHECK(api.clock(&clock)==0);settings.managedTicks=settings.correspondenceQpc=clock.qpc;settings.managedFrequency=clock.frequency;
    const char access[]="OFFLINEONLYACCESS00000001",secret[]="OFFLINEONLYSECRET000000000000000000000001",token[]="OFFLINEONLYSESSIONTOKEN00000001";
    SbrecCredentialUpdate credentials{access,sizeof(access)-1,secret,sizeof(secret)-1,token,sizeof(token)-1,clock.unix100ns/10000000+600};
    uint64_t h=0;CHECK(api.create(&h)==0);
    struct StopGuard {RunnerApi* api;uint64_t handle;~StopGuard(){if(handle)api->request_stop(handle);}} guard{&api,h};
    if(configured){
        auto mediaConfig=reinterpret_cast<decltype(&sbrec_runner_configure_media_v1)>(GetProcAddress(api.module,"sbrec_runner_configure_media_v1"));
        SbrecMediaConfigV1 standard;CHECK(mediaConfig&&mediaConfig(h,&standard)==RUN_OK);
    }
    auto startTime=Steady::now();uint64_t firstSdkVideo=0,firstSdkAudio=0;uint32_t phasesSeen=0;
    uint64_t sdkFrameRejects=0,sdkTimestampRejects=0;bool sdkUtcBoundValid=true;
    auto inspectSdk=[&](const SbrecObservation& row){
        startup_phase(row,phasesSeen);
        if(row.kind!=SBREC_FRAME)return;
        if(row.clock.uncertainty100ns<SbrecSdkUtcQuantization100ns)sdkUtcBoundValid=false;
        if(row.result){++sdkFrameRejects;if(row.result==0x30000005u||row.result==0x32000002u)++sdkTimestampRejects;}
    };
    CHECK(api.start(h,&settings,&credentials)==RUN_INVALID); // generated duration bound unchanged
    CHECK(api.enable_external_v1(h,2)==RUN_INVALID);CHECK(api.enable_external_v1(h,1)==0);
    CHECK(api.enable_clock_v2(h,2,256,8,1000)==RUN_BUSY);CHECK(api.start(h,&settings,&credentials)==0);
    SbrecExternalStatusV1 input{};input.size=sizeof(input);input.version=1;
    SbrecRunStatus status{};status.size=sizeof(status);
    auto until=Steady::now()+std::chrono::seconds((controlDelay||lateSuccess)?40:18);
    bool controlEntered=false,cancelSent=false;
    while(Steady::now()<until) {api.status(h,&status);if(status.state==RUN_TERMINAL)break;
        if(controlDelay||lateSuccess){
            uint32_t requests=0,media=0,acks=0;uint64_t bytes=0;CHECK(counts(&requests,&media,&acks,&bytes)==0);
            if(requests&&!controlEntered){controlEntered=true;printf("{\"stage\":\"sdk-control-entered\",\"milliseconds\":%lld}\n",std::chrono::duration_cast<std::chrono::milliseconds>(Steady::now()-startTime).count());}
            if(controlCancel&&controlEntered&&!cancelSent){CHECK(api.request_stop(h)==RUN_OK);cancelSent=true;}
        }
        if(api.external_status_v1(h,&input)==0&&input.ready)break;std::this_thread::sleep_for(std::chrono::milliseconds(2));}
    if(configured)printf("{\"stage\":\"ready-poll\",\"ready\":%u,\"state\":%u,\"milliseconds\":%lld}\n",input.ready,status.state,std::chrono::duration_cast<std::chrono::milliseconds>(Steady::now()-startTime).count());
    std::vector<uint8_t> pixels((settings.width*4+16)*settings.height,0x7f);std::vector<float> pcm(480*2);
    fill_pixels(pixels,settings.width,settings.height);
    SbrecExternalInputV1 v{};v.size=sizeof(v);v.version=1;v.track=INPUT_VIDEO;v.provenance=INPUT_GENERATED;v.width=settings.width;v.height=settings.height;v.strideBytes=settings.width*4+16;v.bytes=static_cast<uint32_t>(pixels.size());v.data=pixels.data();v.durationNs=GST_SECOND/30;
    SbrecExternalInputV1 a{};a.size=sizeof(a);a.version=1;a.track=INPUT_AUDIO;a.provenance=INPUT_GENERATED;a.sampleFrames=480;a.channels=2;a.rate=48000;a.bytes=static_cast<uint32_t>(pcm.size()*sizeof(float));a.data=pcm.data();
    uint64_t acceptedV=0,acceptedA=0,drops=0,maxCallNs=0,events=0;uint32_t videoFrame=0,audioBlock=0;
    if(!noInput) {
        CHECK(input.ready);CHECK(api.release(h)==RUN_BUSY);
        v.captureQpc=input.originQpc;auto bad=v;bad.strideBytes=1;CHECK(api.push_external_v1(h,&bad)==RUN_INVALID);
        bad=v;bad.bytes=1;CHECK(api.push_external_v1(h,&bad)==RUN_INVALID);
        CHECK(api.push_external_v1(h+1,&v)==RUN_INVALID);
        a.captureQpc=input.originQpc;auto badAudio=a;badAudio.channels=3;CHECK(api.push_external_v1(h,&badAudio)==RUN_INVALID);
        badAudio=a;badAudio.rate=44100;CHECK(api.push_external_v1(h,&badAudio)==RUN_INVALID);
        pcm[0]=NAN;CHECK(api.push_external_v1(h,&a)==RUN_INVALID);pcm[0]=0;
        if(mode=="success"||mode=="retirement"||configured||converted) {
            uint32_t audioLimit=converted?static_cast<uint32_t>(convertedPcm.chunks.size()):200;
            uint32_t videoLimit=converted?static_cast<uint32_t>((convertedPcm.end*30+47999)/48000):60;
            while(videoFrame<videoLimit||audioBlock<audioLimit) {
                CHECK(api.clock(&clock)==0);
                uint64_t audioOffset=converted&&audioBlock<audioLimit?convertedPcm.chunks[audioBlock].position*input.frequency/48000:audioBlock*input.frequency/100;
                if(audioBlock<audioLimit && clock.qpc>=input.originQpc+audioOffset) {
                    a.captureQpc=input.originQpc+audioOffset;
                    if(converted){auto c=convertedPcm.chunks[audioBlock];a.sampleFrames=static_cast<uint32_t>(c.frames);a.bytes=static_cast<uint32_t>(c.frames*8);a.data=convertedPcm.pcm.data()+c.offset*2;}
                    else for(uint32_t n=0;n<480;n++){float sample=0.25f*std::sin(2*3.141592653589793*440*(audioBlock*480+n)/48000);pcm[n*2]=pcm[n*2+1]=sample;}
                    auto before=Steady::now();auto result=api.push_external_v1(h,&a);auto ns=std::chrono::duration_cast<std::chrono::nanoseconds>(Steady::now()-before).count();if(static_cast<uint64_t>(ns)>maxCallNs)maxCallNs=ns;
                    if(configured&&audioBlock==0)printf("{\"stage\":\"first-audio-push\",\"result\":%u,\"milliseconds\":%lld}\n",result,std::chrono::duration_cast<std::chrono::milliseconds>(Steady::now()-startTime).count());
                    CHECK(result==RUN_OK||result==RUN_BUSY);if(result==RUN_OK)++acceptedA;else ++drops;++audioBlock;
                    if(converted)CHECK(result==RUN_OK);
                }
                if(videoFrame<videoLimit && clock.qpc>=input.originQpc+videoFrame*input.frequency/30) {
                    v.captureQpc=input.originQpc+videoFrame*input.frequency/30;
                    fill_pixels(pixels,settings.width,settings.height);
                    auto before=Steady::now();auto result=api.push_external_v1(h,&v);auto ns=std::chrono::duration_cast<std::chrono::nanoseconds>(Steady::now()-before).count();if(static_cast<uint64_t>(ns)>maxCallNs)maxCallNs=ns;
                    if(configured&&videoFrame==0)printf("{\"stage\":\"first-video-push\",\"result\":%u,\"milliseconds\":%lld}\n",result,std::chrono::duration_cast<std::chrono::milliseconds>(Steady::now()-startTime).count());
                    CHECK(result==RUN_OK||result==RUN_BUSY);if(result==RUN_OK)++acceptedV;else ++drops;++videoFrame;
                    // Caller ownership resumes immediately: poison the pixels
                    // while encoder/SDK threads still own the copied native buffer.
                    std::fill(pixels.begin(),pixels.end(),0);
                }
                SbrecExternalObservationV1 rows[64];uint32_t n=0;if(api.drain_external_v1(h,rows,64,&n)==0){events+=n;for(uint32_t i=0;i<n;i++){CHECK(rows[i].version==1&&rows[i].provenance==INPUT_GENERATED);CHECK(rows[i].submissionQpc>=rows[i].captureQpc);}}
                if(converted){
                    for(uint32_t i=0;i<n;i++)printf("{\"stage\":\"external\",\"track\":%u,\"result\":%u,\"ptsNs\":%llu,\"durationNs\":%llu,\"bytes\":%llu,\"discontinuity\":%u}\n",rows[i].track,rows[i].result,rows[i].ptsNs,rows[i].durationNs,rows[i].bytes,rows[i].discontinuity);
                }
                if(configured||converted){
                    SbrecObservation sdk[256];uint32_t n=0;CHECK(api.drain_observations(h,sdk,256,&n)==RUN_OK);
                    for(uint32_t i=0;i<n;i++)inspectSdk(sdk[i]);
                    if(converted)for(uint32_t i=0;i<n;i++)if(sdk[i].kind==OBS_ENCODED||sdk[i].kind==SBREC_FRAME)printf("{\"stage\":\"encoded-sdk\",\"kind\":%u,\"track\":%u,\"result\":%u,\"ptsNs\":%llu,\"runningNs\":%llu,\"sdkPts100ns\":%llu}\n",sdk[i].kind,sdk[i].track,sdk[i].result,sdk[i].ptsNs,sdk[i].runningNs,sdk[i].sdkPts100ns);
                    for(uint32_t i=0;i<n;i++)if(sdk[i].kind==SBREC_FRAME&&!sdk[i].result){
                        CHECK(sdk[i].clock.uncertainty100ns>=SbrecSdkUtcQuantization100ns);
                        auto& first=sdk[i].track==1?firstSdkVideo:firstSdkAudio;
                        if(!first){first=sdk[i].clock.qpc;printf("{\"stage\":\"first-sdk-frame\",\"track\":%u,\"qpc\":%llu,\"milliseconds\":%lld}\n",sdk[i].track,first,std::chrono::duration_cast<std::chrono::milliseconds>(Steady::now()-startTime).count());}
                    }
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(150));
        } else if(mode=="overload") {
            v.durationNs=1000000;for(uint32_t i=0;i<80;i++){v.captureQpc=input.originQpc+i*input.frequency/1000;auto result=api.push_external_v1(h,&v);CHECK(result==RUN_OK||result==RUN_BUSY);if(result==RUN_BUSY)++drops;}
            CHECK(drops>0);
        }
        if(mode=="retirement") {
            auto gateUntil=Steady::now()+std::chrono::seconds(3);
            do {CHECK(lifecycle(0,life)==0);if(life[0])break;std::this_thread::sleep_for(std::chrono::milliseconds(2));}while(Steady::now()<gateUntil);
            CHECK(life[0]>0); // actual intercepted ACK is inside observer dispatch
        }
        CHECK(api.request_stop(h)==0);CHECK(api.request_stop(h)==0);
        if(mode=="retirement") {
            // SDK stop itself may wait on the held ACK before explicit retirement
            // starts. Assert safety, not an unsupported shutdown-progress deadline.
            std::this_thread::sleep_for(std::chrono::milliseconds(200));CHECK(lifecycle(0,life)==0);
            CHECK(life[0]>0&&life[3]==0&&life[4]==0&&life[5]==0);
            CHECK(api.status(h,&status)==0);CHECK(!status.callbacksQuiescent);
            CHECK(api.release(h)==RUN_BUSY);CHECK(lifecycle(2,life)==0);
        }

        CHECK(api.push_external_v1(h,&v)==RUN_NOT_READY);
    }
    while(Steady::now()<until){CHECK(api.status(h,&status)==0);if(status.state==RUN_TERMINAL)break;std::this_thread::sleep_for(std::chrono::milliseconds(10));}
    if(mode=="retirement-refused") {
        CHECK(status.state==RUN_TERMINAL&&status.workerDone&&!status.callbacksQuiescent);
        CHECK(api.release(h)==RUN_BUSY);guard.handle=0;
        printf("retirement_refused_retained=1 callbacks_quiescent=0 explicit_adapter_unload=0\nPASS checks=%d\n",checks);
        return 0; // isolated child exits with the unconfirmed owner retained
    }
    CHECK(status.state==RUN_TERMINAL&&status.workerDone&&status.callbacksQuiescent);
    if(configured){
        SbrecObservation remaining[64];uint32_t n=0;
        do {CHECK(api.drain_observations(h,remaining,64,&n)==RUN_OK);for(uint32_t i=0;i<n;i++)inspectSdk(remaining[i]);}while(n==64);
        if(lateSuccess)CHECK(phasesSeen==254);
    }
    printf("{\"stage\":\"terminal-reason\",\"reason\":%u}\n",status.reason);
    CHECK(api.external_status_v1(h,&input)==0);CHECK(!input.ready);
    // A held response may exhaust the maintained SDK's own retries BEFORE the
    // candidate setup cap. Keep that earlier initialization failure explicit.
    if(noInput)CHECK(controlTimeout ? (status.reason==RUN_START_TIMEOUT||status.reason==RUN_INIT_FAILED)
        : status.reason==(controlCancel?RUN_CANCELLED:RUN_START_TIMEOUT));
    if(controlDelay){
        uint32_t requests=0,media=0,acks=0;uint64_t bytes=0;CHECK(counts(&requests,&media,&acks,&bytes)==0);
        CHECK(controlEntered&&requests>0&&media==0&&acks==0&&bytes==0);
        CHECK(input.originQpc==0&&input.acceptedVideo==0&&input.acceptedAudio==0);
        printf("{\"stage\":\"sdk-control-terminal\",\"requests\":%u,\"mediaRequests\":%u,\"reason\":%u,\"workerDone\":%u,\"callbacksQuiescent\":%u}\n",requests,media,status.reason,status.workerDone,status.callbacksQuiescent);
    }
    if(configured){CHECK(noInput?(!status.acceptedVideo&&!status.acceptedAudio):(firstSdkVideo&&firstSdkAudio));printf("{\"stage\":\"terminal\",\"everReady\":%u,\"reason\":%u,\"sdkVideo\":%llu,\"sdkAudio\":%llu,\"milliseconds\":%lld}\n",input.originQpc!=0,status.reason,status.acceptedVideo,status.acceptedAudio,std::chrono::duration_cast<std::chrono::milliseconds>(Steady::now()-startTime).count());}
    if(mode=="configured") {
        // SDK timestamp refusals are separate from caller/input drops. Mode8
        // intentionally supplies no Persisted ACK: keep that coverage gap honest
        // while checking every SDK frame result, including final-drain rows.
        printf("{\"stage\":\"sdk-timestamp-control\",\"sdkRejects\":%llu,\"timestampRejects\":%llu,\"gaps\":%llu,\"persistedAcks\":%llu,\"utcQuantizationBound100ns\":%llu}\n",sdkFrameRejects,sdkTimestampRejects,status.gaps,status.persistedAcks,SbrecSdkUtcQuantization100ns);
        CHECK(sdkFrameRejects==0&&sdkTimestampRejects==0&&sdkUtcBoundValid&&status.resets==0&&status.droppedObservations==0);
    }
    printf("mode=%s accepted_video=%llu accepted_audio=%llu dropped_video=%llu dropped_audio=%llu process_contentions=%llu input_observation_drops=%llu max_call_ns=%llu input_events=%llu reason=%u\n",argv[4],input.acceptedVideo,input.acceptedAudio,input.droppedVideo,input.droppedAudio,input.processContentionReturns,input.observationDrops,maxCallNs,events,status.reason);
    if(mode=="retirement") {
        CHECK(lifecycle(0,life)==0);CHECK(life[0]==0&&life[3]>0&&life[4]==1&&life[5]==0);
        uint32_t count=0;uint64_t ackCount=0,total=0;SbrecObservation observations[64];
        do {CHECK(api.drain_observations(h,observations,64,&count)==0);total+=count;
            for(uint32_t i=0;i<count;i++)if(observations[i].kind==SBREC_ACK)++ackCount;
        }while(count==64);
        CHECK(ackCount>0);printf("retirement callback_entered=%u callback_active=%u sdk_retired=%u sink_finalized=%u violations=%u final_observations=%llu final_acks=%llu\n",life[1],life[0],life[3],life[4],life[5],total,ackCount);
    }
    CHECK(api.release(h)==0);CHECK(api.push_external_v1(h,&v)==RUN_INVALID);
    guard.handle=0;
    CHECK(FreeLibrary(api.module));api.module=nullptr;
    CHECK(GetModuleHandleW(L"PlaytestRecordingNativeProof.dll")==nullptr);
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    printf("explicit_adapter_unload=1\n");

    if(mode=="success"||converted) {
        CHECK(acceptedV>=55);if(converted){CHECK(acceptedA==convertedPcm.chunks.size());}else CHECK(acceptedA>=180);CHECK(status.acceptedVideo>=50&&status.acceptedAudio>50);
        // Actual SDK-produced MKV, decoded through the existing GStreamer stack.
        // Inspect generated color halves/PCM amplitude, not just container headers.
        std::string graph="filesrc name=file ! matroskademux name=d d.video_0 ! queue ! h264parse ! d3d11h264dec ! videoconvert ! video/x-raw,format=RGBA ! appsink name=v sync=false emit-signals=true d.audio_0 ! queue ! aacparse ! mfaacdec ! audioconvert ! audio/x-raw,format=F32LE,rate=48000,channels=2 ! appsink name=a sync=false emit-signals=true";
        GError* error=nullptr;auto* pipeline=gst_parse_launch(graph.c_str(),&error);CHECK(pipeline&&!error);
        auto* file=gst_bin_get_by_name(GST_BIN(pipeline),"file");g_object_set(file,"location",argv[2],nullptr);gst_object_unref(file);
        Decoded decoded;auto* vs=gst_bin_get_by_name(GST_BIN(pipeline),"v");auto* as=gst_bin_get_by_name(GST_BIN(pipeline),"a");
        g_signal_connect(vs,"new-sample",G_CALLBACK(video_sample),&decoded);g_signal_connect(as,"new-sample",G_CALLBACK(audio_sample),&decoded);
        auto* bus=gst_element_get_bus(pipeline);CHECK(gst_element_set_state(pipeline,GST_STATE_PLAYING)!=GST_STATE_CHANGE_FAILURE);
        auto* message=gst_bus_timed_pop_filtered(bus,15*GST_SECOND,static_cast<GstMessageType>(GST_MESSAGE_ERROR|GST_MESSAGE_EOS));CHECK(message&&GST_MESSAGE_TYPE(message)==GST_MESSAGE_EOS);
        gst_message_unref(message);gst_element_set_state(pipeline,GST_STATE_NULL);gst_object_unref(bus);gst_object_unref(vs);gst_object_unref(as);gst_object_unref(pipeline);
        double rms=std::sqrt(decoded.energy/decoded.audio);
        if(converted){double frames=decoded.audio/2.0;double left=std::sqrt(decoded.leftEnergy/frames),right=std::sqrt(decoded.rightEnergy/frames);CHECK(std::abs(left-.1767767)<.02&&std::abs(right-.0883883)<.02);printf("{\"stage\":\"converted-summary\",\"inputFileFrames\":%llu,\"timelineEndFrame\":%llu,\"inputChunks\":%llu,\"decodedFrames\":%llu,\"leftRms\":%.9f,\"rightRms\":%.9f,\"firstAudioPtsNs\":%llu,\"lastAudioEndNs\":%llu,\"audioGaps\":%llu,\"maxAudioGapNs\":%llu,\"decodeEOS\":true,\"retiredReleasedUnloaded\":true}\n",static_cast<uint64_t>(convertedPcm.pcm.size()/2),convertedPcm.end,static_cast<uint64_t>(convertedPcm.chunks.size()),decoded.audio/2,left,right,decoded.firstAudio,decoded.lastAudioEnd,decoded.audioGaps,decoded.maxAudioGap);}
        printf("decoded_video=%llu decoded_audio_samples=%llu top_red=%.2f top_blue=%.2f bottom_red=%.2f bottom_blue=%.2f audio_rms=%.5f\n",decoded.video,decoded.audio,decoded.topRed/decoded.video,decoded.topBlue/decoded.video,decoded.bottomRed/decoded.video,decoded.bottomBlue/decoded.video,rms);
        CHECK(decoded.valid&&decoded.video>=50&&decoded.audio>100000);CHECK(decoded.topRed/decoded.video>160&&decoded.topBlue/decoded.video<80&&decoded.bottomBlue/decoded.video>160&&decoded.bottomRed/decoded.video<80);CHECK(rms>0.12&&rms<0.24);
    }
    printf("PASS checks=%d\n",checks);return 0;
}
