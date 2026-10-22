#include "SbrecSenderPacing.h"
#include <thread>
#include <vector>
#include <atomic>
#include <cstdio>
#define CHECK(x) do{if(!(x)){printf("FAIL line=%d\n",__LINE__);return 1;}++checks;}while(0)
static int cancelled(void* p){return InterlockedCompareExchange((volatile LONG*)p,0,0)!=0;}
int main(){
 int checks=0;SbrecSenderPacer p;SbrecSenderConfig c=sbrecSenderDefaults();uint32_t n=0;
 CHECK(sbrecSenderInit(&p,c,0,1000000));
 CHECK(sbrecSenderReserve(&p,0,16384,&n)&&n==0);
 CHECK(sbrecSenderReserve(&p,1000000,16384,&n)&&n==4096);
 // A slow/in-flight read occupies all burst capacity across every attempt.
 uint32_t other=0;CHECK(sbrecSenderReserve(&p,20000000,16384,&other)&&!other);
 CHECK(sbrecSenderFinish(&p,20000000,n,4096));
 CHECK(sbrecSenderReserve(&p,20000000,16384,&n)&&!n);
 CHECK(sbrecSenderReserve(&p,21000000,16384,&n)&&n==4096);
 CHECK(sbrecSenderFinish(&p,21000000,n,512));
 CHECK(p.credit==3584&&p.refunded==3584&&p.used==4608);
 CHECK(sbrecSenderReserve(&p,20999999,4096,&n)&&!n&&p.last==21000000&&p.credit==3584);
 // A delayed completion is also valid when another attempt sampled later.
 CHECK(sbrecSenderReserve(&p,22000000,4096,&n)&&n==4096);
 CHECK(sbrecSenderReserve(&p,22000010,4096,&other)&&!other);
 CHECK(sbrecSenderFinish(&p,22000000,n,4096)&&p.reserved==0&&p.last==22000010);
 CHECK(!sbrecSenderFinish(&p,21000000,1,2));
 // Actual concurrent attempts share one grant, not one initial bucket each.
 CHECK(sbrecSenderInit(&p,c,0,1000000));std::atomic<unsigned> total{0};
 std::vector<std::thread> threads;
 for(int i=0;i<8;i++)threads.emplace_back([&]{uint32_t q=0;sbrecSenderReserve(&p,1000000,4096,&q);total+=q;});
 for(auto& t:threads)t.join();CHECK(total==4096&&p.reserved==4096);
 CHECK(sbrecSenderFinish(&p,1000000,4096,4096));
 // Deterministic workload verifies every100ms window, not only a run total.
 CHECK(sbrecSenderInit(&p,c,0,1000000));uint64_t sum=0;std::vector<uint32_t> emitted;
 for(uint64_t tick=1;tick<=2000;tick++){
  CHECK(sbrecSenderReserve(&p,tick*1000,16384,&n));emitted.push_back(n);sum+=n;
  if(n)CHECK(sbrecSenderFinish(&p,tick*1000,n,n));
  uint64_t window=0;for(size_t j=emitted.size()>100?emitted.size()-100:0;j<emitted.size();j++)window+=emitted[j];
  CHECK(window<=4096+(uint64_t)c.bytesPerSecond/10);
  CHECK(sum<=4096+(tick*1000*c.bytesPerSecond)/1000000);
 }
 SbrecSenderConfig bad=c;bad.bytesPerSecond=0;CHECK(!sbrecSenderInit(&p,bad,0,1000000));
 bad=c;bad.capacityBytes=0;CHECK(!sbrecSenderInit(&p,bad,0,1000000));
 bad=c;bad.readBytes=8192;CHECK(!sbrecSenderInit(&p,bad,0,1000000));
 bad=c;bad.maximumWaitMs=101;CHECK(!sbrecSenderInit(&p,bad,0,1000000));
 bad=c;bad.sliceMs=6;CHECK(!sbrecSenderInit(&p,bad,0,1000000));
 CHECK(!sbrecSenderInit(&p,c,0,0));CHECK(!sbrecSenderInit(&p,c,0,1000000000001ULL));
 // A different outstanding attempt forces the real wait. Cancellation must
 // interrupt it without waiting for that attempt or discarding any SDK data.
 LARGE_INTEGER now,freq;QueryPerformanceFrequency(&freq);QueryPerformanceCounter(&now);
 CHECK(sbrecSenderInit(&p,c,now.QuadPart,freq.QuadPart));p.reserved=c.capacityBytes;
 volatile LONG stop=0;int acquired=1;uint32_t grant=0;
 std::thread waiter([&]{acquired=sbrecSenderAcquire(&p,4096,cancelled,(void*)&stop,&grant);});
 auto start=GetTickCount64();while(!InterlockedCompareExchange(&p.waiters,0,0)&&GetTickCount64()-start<200)Sleep(1);
 bool observedWait=p.waiters==1;InterlockedExchange(&stop,1);waiter.join();CHECK(observedWait);
 CHECK(!acquired&&!grant&&p.cancelled==1&&!p.waiters&&GetTickCount64()-start<200);
 QueryPerformanceCounter(&now);c.maximumWaitMs=20;
 CHECK(sbrecSenderInit(&p,c,now.QuadPart,freq.QuadPart));p.reserved=c.capacityBytes;stop=0;
 CHECK(!sbrecSenderAcquire(&p,4096,cancelled,(void*)&stop,&grant)&&p.timeouts==1&&!p.waiters);
 printf("{\"checks\":%d,\"concurrentAttempts\":8,\"windowChecks\":2000,\"cancellation\":true,\"timeout\":true}\n",checks);
 return 0;
}
