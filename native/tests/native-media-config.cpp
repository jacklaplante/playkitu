// D7 CREATED-only numeric configuration checks. No pipeline/SDK/device is started.
#include "../native-synthetic-runner.h"
#include <windows.h>
#include <cstdio>
#define CHECK(x) do{if(!(x)){printf("FAIL line=%d\n",__LINE__);return 1;}}while(0)
int main(int argc,char**argv){
 if(argc!=2)return 2;
 static_assert(sizeof(SbrecRunSettings)==1304,"start ABI1 remains unchanged");
 static_assert(sizeof(SbrecMediaConfigV1)==64,"configure v1 numeric layout");
 auto dll=LoadLibraryA(argv[1]);CHECK(dll);
 auto create=(decltype(&sbrec_runner_create))GetProcAddress(dll,"sbrec_runner_create");
 auto config=(decltype(&sbrec_runner_configure_media_v1))GetProcAddress(dll,"sbrec_runner_configure_media_v1");
 auto release=(decltype(&sbrec_runner_release))GetProcAddress(dll,"sbrec_runner_release");CHECK(create&&config&&release);
 uint64_t h=0;CHECK(create(&h)==RUN_OK);SbrecMediaConfigV1 c;
 CHECK(config(h,nullptr)==RUN_INVALID);CHECK(config(h+1,&c)==RUN_INVALID);
 auto bad=c;bad.audioBps=64000;CHECK(config(h,&bad)==RUN_INVALID);
 bad=c;bad.size=60;CHECK(config(h,&bad)==RUN_INVALID);
 bad=c;bad.version=2;CHECK(config(h,&bad)==RUN_INVALID);
 bad=c;bad.encoder=2;CHECK(config(h,&bad)==RUN_INVALID);
 bad=c;bad.maxVideoKbps=100;CHECK(config(h,&bad)==RUN_INVALID);
 bad=c;bad.videoQueue=31;CHECK(config(h,&bad)==RUN_INVALID);
 bad=c;bad.inputAudio=9;CHECK(config(h,&bad)==RUN_INVALID);
 bad=c;bad.gopFrames=61;CHECK(config(h,&bad)==RUN_INVALID);
 bad=c;bad.mediaBps=65536;bad.videoKbps=bad.maxVideoKbps=200;bad.waitMs=20;CHECK(config(h,&bad)==RUN_INVALID);
 CHECK(config(h,&c)==RUN_OK);CHECK(release(h)==RUN_OK);FreeLibrary(dll);
 puts("{\"createdOnly\":true,\"started\":false,\"invalidCases\":11,\"oldSettingsBytes\":1304,\"configBytes\":64}");return 0;
}
