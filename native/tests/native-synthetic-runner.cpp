#include "../native-synthetic-runner.h"
#include <windows.h>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <string>
#include <thread>

static int checks=0;
#define CHECK(x) do { ++checks; if(!(x)) { printf("FAIL line=%d expression=%s\n",__LINE__,#x); return 1; } } while(0)
int main(int argc,char** argv) {
    // Fake fixtures are compiled here only. No credential discovery, sockets,
    // Steam, Unity or production plugin state transitions in this test program.
    if(argc!=5) return 2;
    HMODULE plugin=LoadLibraryA(argv[1]); CHECK(plugin);
    auto configure=reinterpret_cast<uint32_t(__cdecl*)(uint32_t,const char*)>(GetProcAddress(plugin,"sbrec_test_configure"));
    auto counts=reinterpret_cast<uint32_t(__cdecl*)(uint32_t*,uint32_t*,uint32_t*,uint64_t*)>(GetProcAddress(plugin,"sbrec_test_counts"));
    CHECK(configure && counts); // production plugin is rejected before start
    std::string test=argv[4];
    CHECK(configure(test=="init-fail" ? 2 : (test=="timeout" || test=="sdk-timeout") ? 3 : test=="received" ? 4 : 1,argv[2])==0);
    SbrecClock clock{}; CHECK(sbrec_runner_clock(&clock)==0); CHECK(clock.frequency && clock.qpc && clock.unix100ns);
    SbrecRunSettings settings{}; settings.size=sizeof(settings); settings.seconds=3; settings.width=640; settings.height=360; settings.fps=30;
    settings.startupMs=9000; settings.flushMs=2000; settings.observationCapacity=test=="overflow" ? 1 : 1024;
    settings.encodedByteLimit=test=="byte-limit" ? 1 : 64ULL*1024*1024;
    strcpy_s(settings.sessionId,"00000000-0000-4000-8000-000000000001");
    strcpy_s(settings.runId,"00000000-0000-4000-8000-000000000002");
    strcpy_s(settings.streamName,"sbrec-offline"); strcpy_s(settings.caFile,argv[3]);
    settings.managedFrequency=clock.frequency; settings.managedTicks=clock.qpc;
    settings.correspondenceQpc=clock.qpc; settings.correspondenceUncertainty100ns=clock.uncertainty100ns;
    if(test=="timeout") settings.startupMs=100;
    if(test=="sdk-timeout") settings.startupMs=6000;
    const char access[]="OFFLINEONLYACCESS00000001",secret[]="OFFLINEONLYSECRET000000000000000000000001",token[]="OFFLINEONLYSESSIONTOKEN00000001";
    SbrecCredentialUpdate credentials{access,sizeof(access)-1,secret,sizeof(secret)-1,token,sizeof(token)-1,clock.unix100ns/10000000+600};
    if(test=="expired") credentials.expiresUnixSeconds=clock.unix100ns/10000000;
    uint64_t handle=0,other=0; CHECK(sbrec_runner_create(&handle)==0); CHECK(handle); CHECK(sbrec_runner_create(&other)==RUN_BUSY);
    CHECK(sbrec_runner_start(handle,&settings,&credentials)==0);
    CHECK(sbrec_runner_start(handle,&settings,&credentials)==RUN_BUSY);
    CHECK(sbrec_runner_release(handle)==RUN_BUSY);
    if(test=="cancel") { CHECK(sbrec_runner_request_stop(handle)==0); CHECK(sbrec_runner_request_stop(handle)==0); }
    SbrecRunStatus status{}; status.size=sizeof(status);
    auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(25);
    bool replaced=false,stopped=false; uint64_t previous=0,observed=0,sdkFrames=0,videoFrames=0,audioFrames=0,ackReceived=0,ackPersisted=0;
    printf("abi_settings_bytes=%zu abi_status_bytes=%zu abi_observation_bytes=%zu\n",sizeof(settings),sizeof(status),sizeof(SbrecObservation));
    SbrecObservation firstVideo{},firstEncoded{};
    while(std::chrono::steady_clock::now()<deadline) {
        CHECK(sbrec_runner_status(handle,&status)==0);
        if(test=="replace" && status.acceptedVideo>10 && !replaced) {
            ++credentials.expiresUnixSeconds; CHECK(sbrec_runner_update_credentials(handle,&credentials)==0); replaced=true;
        }
        if(test=="stop" && status.acceptedVideo>10 && !stopped) {
            auto begin=std::chrono::steady_clock::now(); CHECK(sbrec_runner_request_stop(handle)==0); CHECK(sbrec_runner_request_stop(handle)==0);
            CHECK(std::chrono::steady_clock::now()-begin<std::chrono::milliseconds(100)); stopped=true;
        }
        if(test!="overflow" || status.state==RUN_TERMINAL) {
            SbrecObservation events[256]; uint32_t n=0; CHECK(sbrec_runner_drain_observations(handle,events,256,&n)==0);
            for(uint32_t i=0;i<n;i++) {
                auto& e=events[i]; CHECK(e.sequence>previous); previous=e.sequence; ++observed;
                if(e.kind==1) { ++sdkFrames; if(e.track==1) { if(!videoFrames++) firstVideo=e; } else if(e.track==2) ++audioFrames; }
                if(e.kind==11 && e.track==1 && !firstEncoded.sequence) firstEncoded=e;
                if(e.kind==2 && e.flags==2) ++ackReceived;
                if(e.kind==2 && e.flags==3) ++ackPersisted;
            }
        }
        if(status.state==RUN_TERMINAL) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    CHECK(status.state==RUN_TERMINAL); CHECK(status.workerDone && status.callbacksQuiescent);
    uint32_t api=0,media=0,acks=0; uint64_t bytes=0; CHECK(counts(&api,&media,&acks,&bytes)==0);
    printf("case=%s state=%u reason=%u degraded=%u checks=%d api=%u media=%u parsed_acks=%u mkv_bytes=%llu\n",argv[4],status.state,status.reason,status.degraded,checks,api,media,acks,bytes);
    printf("video=%llu audio=%llu source=%llu encoded_bytes=%llu received=%llu persisted=%llu drops=%llu observed=%llu\n",status.acceptedVideo,status.acceptedAudio,status.sourceFrames,status.encodedBytes,status.receivedAcks,status.persistedAcks,status.droppedObservations,observed);
    printf("video_running_ns=%llu video_sdk_100ns=%llu last_video_running_ns=%llu last_video_sdk_100ns=%llu source_qpc=%llu source_utc_100ns=%llu qpc_frequency=%llu\n",status.firstVideoRunningNs,status.firstVideoSdk100ns,status.lastVideoRunningNs,status.lastVideoSdk100ns,status.firstSource.qpc,status.firstSource.unix100ns,status.firstSource.frequency);
    printf("encoded_pts_ns=%llu segment_start_ns=%llu segment_base_ns=%llu running_ns=%llu sdk_input_running_ns=%llu\n",firstEncoded.ptsNs,firstEncoded.segmentStartNs,firstEncoded.segmentBaseNs,firstEncoded.runningNs,firstVideo.runningNs);
    if(test=="success" || test=="replace" || test=="overflow" || test=="received") {
        CHECK(status.acceptedVideo>60 && status.acceptedAudio>100 && status.sourceFrames>60 && status.sourceFrames<=90); CHECK(status.encodedBytes>1000);
        CHECK(api>=3 && media>=1 && bytes>1000); CHECK(status.receivedAcks>0);
        if(test=="received") CHECK(status.persistedAcks==0 && status.degraded && status.gaps>0); else CHECK(status.persistedAcks>0);
        CHECK(status.firstVideoRunningNs!=~0ULL && status.firstAudioRunningNs!=~0ULL);
        CHECK(status.lastVideoRunningNs>status.firstVideoRunningNs && status.lastVideoSdk100ns>status.firstVideoSdk100ns);
        if(test=="overflow") CHECK(status.droppedObservations>0); else {
            CHECK(videoFrames>0 && audioFrames>0 && ackReceived>0);
            CHECK(firstEncoded.runningNs==firstVideo.runningNs);
            CHECK(firstVideo.sdkPts100ns*100>firstVideo.runningNs);
        }
    }
    if(test=="replace") CHECK(replaced && status.credentialRevision==2 && status.credentialResult==0);
    if(test=="byte-limit") CHECK(status.reason==RUN_BYTE_LIMIT && status.encodedBytes==0);
    if(test=="expired") CHECK(status.reason==RUN_CREDENTIAL_FAILED && api==0);
    if(test=="timeout" || test=="sdk-timeout") CHECK(status.reason==RUN_START_TIMEOUT);
    if(test=="init-fail") CHECK(status.reason==RUN_INIT_FAILED || status.reason==RUN_START_TIMEOUT);
    if(test=="cancel" || test=="stop") CHECK(status.reason==RUN_CANCELLED);
    const uint64_t stable=status.sourceFrames;
    CHECK(sbrec_runner_request_stop(handle)==0); CHECK(sbrec_runner_update_credentials(handle,&credentials)==RUN_NOT_READY);
    std::this_thread::sleep_for(std::chrono::milliseconds(100)); CHECK(sbrec_runner_status(handle,&status)==0); CHECK(status.sourceFrames==stable);
    CHECK(sbrec_runner_release(handle)==0); CHECK(sbrec_runner_status(handle,&status)==RUN_INVALID); CHECK(sbrec_runner_release(handle)==RUN_INVALID);
    CHECK(sbrec_runner_create(&other)==0 && other!=handle); CHECK(sbrec_runner_request_stop(other)==0); CHECK(sbrec_runner_release(other)==0);
    printf("PASS checks=%d\n",checks); return 0;
}
