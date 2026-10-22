// D2 generated-only SDK backpressure fixture. Load the accepted runner dynamically;
// the offline plugin must expose test controls before any run can be started.
#include "../native-synthetic-runner.h"
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <chrono>
#include <thread>
#define CHECK(x) do {if(!(x)){printf("FAIL line=%d\n",__LINE__);return 1;}}while(0)
int main(int argc,char** argv) {
    if(argc<6||argc>8)return 2;
    const bool rejectGopFirst=argc==8&&strcmp(argv[7],"reject-gop-first")==0;
    if(argc==8&&!rejectGopFirst)return 2;
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX);
    HMODULE runner=LoadLibraryA(argv[1]),plugin=LoadLibraryA(argv[2]);CHECK(runner&&plugin);
#define BIND(name) auto name=reinterpret_cast<decltype(&sbrec_runner_##name)>(GetProcAddress(runner,"sbrec_runner_" #name));CHECK(name)
    BIND(clock);BIND(create);BIND(start);BIND(status);BIND(drain_observations);BIND(request_stop);BIND(release);
#undef BIND
    auto configure=reinterpret_cast<uint32_t(__cdecl*)(uint32_t,const char*)>(GetProcAddress(plugin,"sbrec_test_configure"));
    auto counts=reinterpret_cast<uint32_t(__cdecl*)(uint32_t*,uint32_t*,uint32_t*,uint64_t*)>(GetProcAddress(plugin,"sbrec_test_counts"));
    CHECK(configure&&counts);
    unsigned mode=strcmp(argv[5],"baseline")==0?5:strcmp(argv[5],"stall")==0?6:strcmp(argv[5],"timeout")==0?7:
        strcmp(argv[5],"baseline-noack")==0?8:strcmp(argv[5],"stall-noack")==0?9:strcmp(argv[5],"stop-wait")==0?10:strcmp(argv[5],"retry-control")==0?11:strcmp(argv[5],"retry-once")==0?12:strcmp(argv[5],"retry-outage")==0?13:0;
    auto cancelWaiting=reinterpret_cast<uint32_t(__cdecl*)()>(GetProcAddress(plugin,"sbrec_test_cancel_waiting_sender"));
    CHECK(mode!=10||cancelWaiting);
    CHECK(mode&&configure(mode,argv[3])==0);
    std::string base=argv[3];FILE* events=nullptr;FILE* states=nullptr;
    CHECK(fopen_s(&events,(base+".events.csv").c_str(),"wx")==0);
    CHECK(fopen_s(&states,(base+".states.csv").c_str(),"wx")==0);
    fprintf(events,"qpc,frequency,kind,track,flags,result,ptsNs,runningNs,sdkPts100ns,timecodeMs,bytes\n");
    fprintf(states,"qpc,frequency,state,reason,encodedBytes,acceptedVideo,acceptedAudio,sourceFrames,resets,gaps,droppedObservations\n");
    SbrecClock c{};CHECK(clock(&c)==0);
    SbrecRunSettings settings{};settings.size=sizeof(settings);settings.seconds=8;settings.width=640;settings.height=360;settings.fps=30;
    SbrecMediaConfigV1 config;
    bool explicitProfile=argc>=7&&strcmp(argv[6],"legacy")!=0;
    if(argc>=7&&strcmp(argv[6],"standard")==0){settings.width=1280;settings.height=720;}
    else if(argc>=7&&strcmp(argv[6],"low")==0){
        settings.width=640;settings.height=360;settings.fps=15;config.videoKbps=800;config.maxVideoKbps=1000;config.audioBps=96000;
        config.gopFrames=15;config.videoQueue=15;config.audioQueue=50;config.inputVideo=1;config.inputAudio=4;config.mediaBps=163840;
    }
    static_assert(sizeof(SbrecRunSettings)==1304,"unchanged start ABI1");
    settings.startupMs=9000;settings.flushMs=2000;settings.observationCapacity=1024;settings.encodedByteLimit=8ULL*1024*1024;
    strcpy_s(settings.sessionId,"00000000-0000-4000-8000-000000000001");strcpy_s(settings.runId,"00000000-0000-4000-8000-000000000002");
    strcpy_s(settings.streamName,"sbrec-offline");strcpy_s(settings.caFile,argv[4]);
    settings.managedFrequency=c.frequency;settings.managedTicks=c.qpc;settings.correspondenceQpc=c.qpc;settings.correspondenceUncertainty100ns=c.uncertainty100ns;
    const char access[]="OFFLINEONLYACCESS00000001",secret[]="OFFLINEONLYSECRET000000000000000000000001",token[]="OFFLINEONLYSESSIONTOKEN00000001";
    SbrecCredentialUpdate credential{access,sizeof(access)-1,secret,sizeof(secret)-1,token,sizeof(token)-1,c.unix100ns/10000000+600};
    uint64_t h=0;CHECK(create(&h)==0);
    auto configureMedia=reinterpret_cast<decltype(&sbrec_runner_configure_media_v1)>(GetProcAddress(runner,"sbrec_runner_configure_media_v1"));
    if(explicitProfile){
        CHECK(configureMedia);
        if(rejectGopFirst) {
            // A valid numeric config can still disagree with start's FPS. Reject
            // before credentials/global ownership; CREATED release cannot retire
            // an owner that never started. Keep the DLL loaded across every step.
            auto mismatch=config;mismatch.gopFrames=1;
            for(unsigned attempt=0;attempt<2;attempt++) {
                CHECK(configureMedia(h,&mismatch)==RUN_OK);
                CHECK(start(h,&settings,&credential)==RUN_INVALID);
                SbrecRunStatus rejected{};rejected.size=sizeof(rejected);
                CHECK(status(h,&rejected)==RUN_OK&&rejected.state==RUN_CREATED&&
                    !rejected.workerDone&&!rejected.callbacksQuiescent&&!rejected.observations);
                uint32_t api=0,media=0,acks=0;uint64_t bytes=0;
                CHECK(counts(&api,&media,&acks,&bytes)==RUN_OK&&!api&&!media&&!acks&&!bytes);
                if(attempt==0){auto old=h;CHECK(release(h)==RUN_OK);CHECK(create(&h)==RUN_OK&&h!=old);}
            }
            puts("{\"gopRejected\":2,\"createdUnchanged\":true,\"releasedAndRecreated\":true,\"moduleStayedLoaded\":true}");
        }
        auto bad=config;bad.size=0;CHECK(configureMedia(h,&bad)==RUN_INVALID);
        bad=config;bad.version=2;CHECK(configureMedia(h,&bad)==RUN_INVALID);
        bad=config;bad.audioBps=64000;CHECK(configureMedia(h,&bad)==RUN_INVALID);
        bad=config;bad.encoder=2;CHECK(configureMedia(h,&bad)==RUN_INVALID);
        bad=config;bad.mediaBps=65536;bad.videoKbps=bad.maxVideoKbps=200;bad.waitMs=20;CHECK(configureMedia(h,&bad)==RUN_INVALID);
        CHECK(configureMedia(h+1,&config)==RUN_INVALID);
        CHECK(configureMedia(h,&config)==RUN_OK);
        // Only Run's copied snapshot may affect asynchronous construction.
        auto preserved=config;config.audioBps=1;
        CHECK(start(h,&settings,&credential)==0);config=preserved;
        CHECK(configureMedia(h,&config)==RUN_BUSY);
    }else CHECK(start(h,&settings,&credential)==0);
    SbrecRunStatus s{};s.size=sizeof(s);unsigned rows=0,polls=0;bool cancelledWaiting=false;
    const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(23);
    auto outageBegan=std::chrono::steady_clock::time_point::max();bool outageStop=false;uint64_t outageStopQpc=0;
    while(std::chrono::steady_clock::now()<deadline) {
        CHECK(status(h,&s)==0&&clock(&c)==0);
        if(mode==10&&!cancelledWaiting) {
            uint32_t result=cancelWaiting();CHECK(result<=1);
            if(result==1){cancelledWaiting=true;CHECK(request_stop(h)==0);}
        }
        if(mode==13&&!outageStop) {
            uint32_t api=0,media=0,acks=0;uint64_t bytes=0;CHECK(counts(&api,&media,&acks,&bytes)==RUN_OK);
            if(media&&outageBegan==std::chrono::steady_clock::time_point::max())outageBegan=std::chrono::steady_clock::now();
            if(outageBegan!=std::chrono::steady_clock::time_point::max()&&std::chrono::steady_clock::now()-outageBegan>=std::chrono::seconds(4)){
                CHECK(clock(&c)==RUN_OK);outageStopQpc=c.qpc;CHECK(request_stop(h)==RUN_OK);outageStop=true;
                puts("{\"persistentOutageStopRequested\":true,\"afterFirstMediaSeconds\":4}");
            }
        }
        CHECK(++polls<=(mode==10?25000u:500u));
        fprintf(states,"%llu,%llu,%u,%u,%llu,%llu,%llu,%llu,%llu,%llu,%llu\n",c.qpc,c.frequency,s.state,s.reason,s.encodedBytes,s.acceptedVideo,s.acceptedAudio,s.sourceFrames,s.resets,s.gaps,s.droppedObservations);
        SbrecObservation e[256];uint32_t n=0;CHECK(drain_observations(h,e,256,&n)==0);
        for(uint32_t i=0;i<n;i++) {
            CHECK(++rows<=4096);
            fprintf(events,"%llu,%llu,%u,%u,%u,%u,%llu,%llu,%llu,%llu,%llu\n",e[i].clock.qpc,e[i].clock.frequency,e[i].kind,e[i].track,e[i].flags,e[i].result,e[i].ptsNs,e[i].runningNs,e[i].sdkPts100ns,e[i].timecodeMs,e[i].bytes);
        }
        if(s.state==RUN_TERMINAL)break;
        std::this_thread::sleep_for(std::chrono::milliseconds(mode==10?1:50));
    }
    fclose(events);fclose(states);
    CHECK(s.state==RUN_TERMINAL&&s.workerDone&&s.callbacksQuiescent&&release(h)==0);
    if(mode==13){CHECK(clock(&c)==RUN_OK);printf("{\"outageStopRequested\":%s,\"stopToTerminalMilliseconds\":%llu}\n",outageStop?"true":"false",outageStop?(c.qpc-outageStopQpc)*1000/c.frequency:0);}
    uint32_t api=0,media=0,acks=0;uint64_t bytes=0;CHECK(counts(&api,&media,&acks,&bytes)==0&&media>=1);
    if(mode==10)CHECK(cancelledWaiting);
    else if(mode!=7&&mode<12)CHECK(s.acceptedVideo>settings.fps*6&&s.acceptedAudio>200&&bytes>1000&&s.droppedObservations==0&&media==1);
    printf("{\"case\":\"%s\",\"terminal\":true,\"reason\":%u,\"workerDone\":%u,\"callbacksQuiescent\":%u,\"sourceFrames\":%llu,\"acceptedVideo\":%llu,\"acceptedAudio\":%llu,\"encodedBytes\":%llu,\"attemptedPayloadBytes\":%llu,\"mediaCalls\":%u,\"parsedSimulatedAcks\":%u,\"resets\":%llu,\"gaps\":%llu,\"droppedObservations\":%llu,\"eventRows\":%u}\n",argv[5],s.reason,s.workerDone,s.callbacksQuiescent,s.sourceFrames,s.acceptedVideo,s.acceptedAudio,s.encodedBytes,bytes,media,acks,s.resets,s.gaps,s.droppedObservations,rows);
    // Do not FreeLibrary while any SDK/plugin ownership might remain; confirmed
    // runner retirement above quiesces its callbacks, then ordinary process exit.
    return 0;
}
