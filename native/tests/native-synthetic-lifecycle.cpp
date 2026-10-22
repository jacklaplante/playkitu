// Unit-test translation unit of the actual C ABI implementation. The only seam
// pauses start after find() retained ownership, before taking the Run mutex.
#define SBREC_RUNNER_LIFECYCLE_TEST
#include "../native-synthetic-runner.cpp"
#include <cstdio>

namespace {
std::mutex gateMutex;
std::condition_variable gateWake;
bool gateEnabled=false, startFound=false, resumeStart=false;
int checks=0;
}
void sbrec_test_after_find() noexcept {
    std::unique_lock<std::mutex> lock(gateMutex);
    if(!gateEnabled) return;
    startFound=true; gateWake.notify_all();
    gateWake.wait(lock,[]{return resumeStart;});
}
#define CHECK(x) do { ++checks; if(!(x)) { printf("FAIL line=%d expression=%s\n",__LINE__,#x); return 1; } } while(0)
int main(int argc,char** argv) {
    if(argc!=2) return 2;
    // D19 tests the ACTUAL deadline/publication functions without sleeping or
    // inventing SDK acceptance. The later integration fixture supplies real frames.
    {
        Run r;r.external=true;r.settings.startupMs=10000;r.settings.observationCapacity=1024;
        auto start=Steady::now();r.started=start;
        CHECK(!r.startup_expired(start+std::chrono::milliseconds(19999)));
        CHECK(r.startup_expired(start+std::chrono::seconds(20)));
        r.readinessPublished=true;r.inputReadyAt=start+std::chrono::seconds(19);
        CHECK(!r.startup_expired(start+std::chrono::milliseconds(28999)));
        CHECK(r.startup_expired(start+std::chrono::seconds(29)));
        r.inputReadyAt=start+std::chrono::seconds(25); // defensive absolute cap
        CHECK(r.startup_expired(start+std::chrono::seconds(30)));
        r.readinessPublished=false;r.started=Steady::now()-std::chrono::seconds(21);
        CHECK(r.publish_input_ready()==RUN_START_TIMEOUT);CHECK(!r.inputReady&&r.inputStatus.originQpc==0);
    }
    {
        Run r;r.external=true;r.settings.startupMs=10000;r.settings.observationCapacity=1024;r.started=Steady::now();
        CHECK(r.publish_input_ready()==RUN_OK);auto epoch=r.inputReadyAt;
        CHECK(r.publish_input_ready()==RUN_BUSY);CHECK(epoch==r.inputReadyAt);
        r.halt(RUN_CANCELLED);CHECK(r.publish_input_ready()==RUN_CANCELLED);CHECK(epoch==r.inputReadyAt);
    }
    {
        Run r;r.external=true;r.settings.startupMs=10000;r.settings.observationCapacity=1024;r.started=Steady::now();
        r.halt(RUN_CANCELLED);CHECK(r.publish_input_ready()==RUN_CANCELLED);CHECK(!r.inputReady&&r.inputStatus.originQpc==0);
        r.external=false;r.started=Steady::now();CHECK(r.startup_expired(r.started+std::chrono::seconds(10)));
    }
    // Missing public CA sentinel stops any accidentally launched worker BEFORE
    // gst_init; no plugin, SDK, real credentials or socket can be reached here.
    CHECK(!std::filesystem::exists(std::filesystem::u8path(argv[1])));
    SbrecRunSettings settings{}; settings.size=sizeof(settings); settings.seconds=1;
    settings.width=settings.height=64; settings.fps=1; settings.observationCapacity=1;
    settings.startupMs=settings.flushMs=100; settings.encodedByteLimit=1;
    strcpy_s(settings.sessionId,"00000000-0000-4000-8000-000000000001");
    strcpy_s(settings.runId,"00000000-0000-4000-8000-000000000002");
    strcpy_s(settings.streamName,"offline-lifecycle"); strcpy_s(settings.caFile,argv[1]);
    settings.managedFrequency=settings.correspondenceQpc=1;
    const char access[]="OFFLINEONLYACCESS00000001",secret[]="OFFLINEONLYSECRET00000001",token[]="OFFLINEONLYTOKEN";
    SbrecCredentialUpdate credentials{access,sizeof(access)-1,secret,sizeof(secret)-1,token,sizeof(token)-1,1};
    constexpr int iterations=256;
    for(int i=0;i<iterations;i++) {
        uint64_t handle=0,next=0; CHECK(sbrec_runner_create(&handle)==RUN_OK);
        // Exercise retirement with the additive clock observer allocated as
        // well as ABI1-only runs; a retained Run must never be re-enabled.
        if(i%2==0) {
            CHECK(sbrec_runner_enable_clock_v2(handle,2,256,64,1000)==RUN_OK);
            CHECK(sbrec_runner_enable_clock_v2(handle,2,256,64,1000)==RUN_OK);
            CHECK(sbrec_runner_enable_clock_v2(handle,2,256,32,1000)==RUN_BUSY);
        }
        { std::lock_guard<std::mutex> lock(gateMutex); gateEnabled=true; startFound=resumeStart=false; }
        uint32_t started=RUN_INTERNAL;
        std::thread starter([&]{started=sbrec_runner_start(handle,&settings,&credentials);});
        bool reached;
        { std::unique_lock<std::mutex> lock(gateMutex);
          reached=gateWake.wait_for(lock,std::chrono::seconds(2),[]{return startFound;}); }
        // Always unblock and join before asserting, including failure paths.
        uint32_t released=sbrec_runner_release(handle);
        uint32_t recreated=sbrec_runner_create(&next);
        { std::lock_guard<std::mutex> lock(gateMutex); resumeStart=true; gateWake.notify_all(); }
        starter.join();
        { std::lock_guard<std::mutex> lock(gateMutex); gateEnabled=false; }
        CHECK(reached); CHECK(released==RUN_OK); CHECK(recreated==RUN_OK && next!=handle);
        CHECK(started==RUN_BUSY); // retained stale Run is retired before unregister
        CHECK(sbrec_runner_enable_clock_v2(handle,2,256,64,1000)==RUN_INVALID);
        SbrecRunStatus status{}; status.size=sizeof(status);
        CHECK(sbrec_runner_status(handle,&status)==RUN_INVALID);
        CHECK(sbrec_runner_request_stop(handle)==RUN_INVALID);
        CHECK(sbrec_runner_status(next,&status)==RUN_OK && status.state==RUN_CREATED);
        CHECK(!sbrec_native_owned); CHECK(sbrec_runner_release(next)==RUN_OK);
    }
    // Exercise the other ordering too: start wins, so release can only succeed
    // after worker quiescence. Retaining a test reference permits exact state
    // verification even if the missing-file worker already finished very fast.
    for(int i=0;i<iterations;i++) {
        uint64_t handle=0; CHECK(sbrec_runner_create(&handle)==RUN_OK);
        if(i%2==0) CHECK(sbrec_runner_enable_clock_v2(handle,2,256,64,1000)==RUN_OK);
        auto retained=find(handle);
        CHECK(sbrec_runner_start(handle,&settings,&credentials)==RUN_OK);
        uint32_t released=sbrec_runner_release(handle);
        if(released==RUN_BUSY) {
            CHECK(sbrec_runner_request_stop(handle)==RUN_OK);
            const auto deadline=Steady::now()+std::chrono::seconds(2);
            do { released=sbrec_runner_release(handle); if(released==RUN_BUSY) std::this_thread::yield(); }
            while(released==RUN_BUSY && Steady::now()<deadline);
        }
        CHECK(released==RUN_OK);
        { std::lock_guard<std::mutex> lock(retained->mutex);
          CHECK(retained->done && retained->status.workerDone && retained->status.callbacksQuiescent); }
    }
    printf("PASS lifecycle_checks=%d release_wins=%d start_wins=%d missing_ca_before_gst_init=1 sockets=0\n",checks,iterations,iterations);
    return 0;
}
