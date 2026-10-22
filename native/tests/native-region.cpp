// Exercise the actual start/ownership path without reaching gst_init or sockets.
#define SBREC_RUNNER_EXPORT
#include "../native-synthetic-runner.cpp"
#include <cstdio>
#include <cstring>
static int checks=0;
#define CHECK(x) do {++checks;if(!(x)){printf("FAIL region line=%d\n",__LINE__);return 1;}}while(0)
int main(int argc,char** argv) {
    CHECK(argc==2 && !std::filesystem::exists(std::filesystem::u8path(argv[1])));
    CHECK(sbrec_runner_trusted_region_version()==1);
    char region[32]{};
    for(const char* invalid:{"","US-east-1","us-east-12","us-east-1/","us--1","us-ea1-1"," us-east-1","us-east-1 ","us-easttttttttttttttttttttttttttttttttt-1"}) {
        CHECK(SetEnvironmentVariableA("PLAYREEL_AWS_REGION",invalid));CHECK(!sbrec_copy_region(region));
    }
    CHECK(SetEnvironmentVariableA("PLAYREEL_AWS_REGION",nullptr));
    CHECK(SetEnvironmentVariableA("AWS_REGION","us-east-2"));CHECK(!sbrec_copy_region(region));
    SbrecRunSettings settings{};settings.size=sizeof(settings);settings.seconds=1;settings.width=settings.height=64;settings.fps=1;
    settings.observationCapacity=1;settings.startupMs=settings.flushMs=100;settings.encodedByteLimit=1;
    strcpy_s(settings.sessionId,"00000000-0000-4000-8000-000000000001");strcpy_s(settings.runId,"00000000-0000-4000-8000-000000000002");
    strcpy_s(settings.streamName,"offline-region");strcpy_s(settings.caFile,argv[1]);settings.managedFrequency=settings.correspondenceQpc=1;
    const char access[]="OFFLINEONLYACCESS00000001",secret[]="OFFLINEONLYSECRET00000001",token[]="OFFLINEONLYTOKEN";
    SbrecCredentialUpdate credentials{access,sizeof(access)-1,secret,sizeof(secret)-1,token,sizeof(token)-1,1};
    uint64_t handle=0;CHECK(sbrec_runner_create(&handle)==RUN_OK);
    CHECK(sbrec_runner_start(handle,&settings,&credentials)==RUN_INVALID);
    auto run=find(handle);CHECK(run && run->status.state==RUN_CREATED && !run->worker.joinable() && !sbrec_native_owned.load());
    CHECK(SetEnvironmentVariableA("PLAYREEL_AWS_REGION","eu-west-1"));
    CHECK(sbrec_runner_start(handle,&settings,&credentials)==RUN_OK);
    CHECK(SetEnvironmentVariableA("PLAYREEL_AWS_REGION","ap-south-1"));
    CHECK(strcmp(run->awsRegion,"eu-west-1")==0);
    CHECK(sbrec_runner_request_stop(handle)==RUN_OK);
    uint32_t result=RUN_BUSY;
    for(int i=0;i<1000 && result==RUN_BUSY;i++){result=sbrec_runner_release(handle);if(result==RUN_BUSY)std::this_thread::sleep_for(std::chrono::milliseconds(1));}
    CHECK(result==RUN_OK && !sbrec_native_owned.load());
    CHECK(SetEnvironmentVariableA("PLAYREEL_AWS_REGION",nullptr));
    printf("PASS region_checks=%d no_sdk_or_devices=1\n",checks);return 0;
}
