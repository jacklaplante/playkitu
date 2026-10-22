// Test plugin only. Actual pinned Response.c performs normal SDK signing and
// request setup, but neither curl transport primitive can reach a socket.
#include "Include_i.h"
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#undef LOG_CLASS
static CURLcode runner_perform(PCurlResponse);
static CURLcode runner_getinfo(CURL*, CURLINFO, ...);
#define curl_easy_perform(x) runner_perform(pCurlResponse)
#define curl_easy_getinfo runner_getinfo
#define curlCompleteSync observedCurlCompleteSync
#include "Response.c"
#undef curlCompleteSync

static SRWLOCK stateLock=SRWLOCK_INIT;
static UINT64 keys[256]; static UINT32 keyHead=0,keyCount=0;
static UINT32 mode=0; static CHAR output[2048];
static volatile LONG calls=0, mediaCalls=0, parsedAcks=0;
static UINT64 totalBytes=0;
// D19 test-only setup outage. Normal SDK request timeouts/retries/cancellation
// remain in charge; successful fixed responses become available after this gate.
static UINT32 setupDelayMs=0;
static UINT64 setupAvailableAt=0;
__declspec(dllexport) UINT32 __cdecl sbrec_test_setup_delay(UINT32 milliseconds) {
    if(milliseconds>15000)return 1;
    AcquireSRWLockExclusive(&stateLock);setupDelayMs=milliseconds;setupAvailableAt=0;ReleaseSRWLockExclusive(&stateLock);return 0;
}
// D8 identities are bounded numeric upload handles, never URLs, headers or pointers.
static UINT64 attemptUploads[8];
static PCurlApiCallbacks reconnectOwner=NULL;
static __declspec(thread) long interceptedHttpStatus=200;
static PCurlResponse senderResponse=NULL; /* borrowed only under stateLock */
static volatile LONG foreignReadRejected=0;
// D4 drives the real request cancellation atomics while the production pacer
// waits. The fixture lease is cleared before its SDK response can be retired.
__declspec(dllexport) UINT32 __cdecl sbrec_test_cancel_waiting_sender(void) {
    UINT32 cancelled=0;
    AcquireSRWLockShared(&stateLock);
    if(senderResponse && InterlockedCompareExchange(&senderResponse->pCurlRequest->pCurlApiCallbacks->senderPacer.waiters,0,0)>0){
        CHAR scratch[1];
        if(postReadCallback(scratch,1,1,senderResponse->pCurlRequest)!=CURL_READFUNC_ABORT){ReleaseSRWLockShared(&stateLock);return 2;}
        InterlockedIncrement(&foreignReadRejected);
        ATOMIC_STORE_BOOL(&senderResponse->pCurlRequest->requestInfo.terminating,TRUE);cancelled=1;
    }
    ReleaseSRWLockShared(&stateLock);return cancelled;
}
// Existing maintained SDK read-hook observes the unmodified return value before
// Response.c translates it into CURL_READFUNC_ABORT. TLS keeps streaming reads
// separate from other SDK threads; no bytes, pointers or credentials are retained.
static __declspec(thread) STATUS observedReadStatus;
static __declspec(thread) UINT32 observedReadBytes, observedReadCalls;
static STATUS observe_read(PCurlResponse response, UPLOAD_HANDLE upload, PBYTE bytes,
                           UINT32 capacity, PUINT32 written, STATUS status) {
    observedReadStatus=status; observedReadBytes=*written; ++observedReadCalls;
    return status;
}
// C7 test-only lifecycle gate, exercised by an actual SDK ACK callback. These
// exports/counters are never part of the production kvssink artifact.
static volatile LONG holdAck=0, activeAck=0, enteredAck=0, retiring=0, retired=0, finalized=0, lifecycleErrors=0;
static volatile LONG refuseRetirement=0;
__declspec(dllexport) UINT32 __cdecl sbrec_test_lifecycle_control(UINT32 command, UINT32* values) {
    if(command==1) InterlockedExchange(&holdAck,1);
    else if(command==2) InterlockedExchange(&holdAck,0);
    else if(command==3) InterlockedExchange(&refuseRetirement,1);
    else if(command!=0) return 1;
    if(!values) return 1;
    values[0]=activeAck;values[1]=enteredAck;values[2]=retiring;values[3]=retired;values[4]=finalized;values[5]=lifecycleErrors;
    return 0;
}
UINT32 sbrec_test_retire_allowed(void) { return !InterlockedCompareExchange(&refuseRetirement,0,0); }
void sbrec_test_lifecycle_event(UINT32 event) {
    if(event==1) {
        InterlockedIncrement(&activeAck);InterlockedIncrement(&enteredAck);
        while(InterlockedCompareExchange(&holdAck,0,0)) Sleep(1);
    } else if(event==2) InterlockedDecrement(&activeAck);
    else if(event==3) InterlockedIncrement(&retiring);
    else if(event==4) {if(activeAck)InterlockedIncrement(&lifecycleErrors);InterlockedIncrement(&retired);}
    else if(event==5) {if(activeAck||!retired)InterlockedIncrement(&lifecycleErrors);InterlockedIncrement(&finalized);}
    else if(event==6) InterlockedIncrement(&lifecycleErrors); // unexpected callback ownership after SDK teardown
}
__declspec(dllexport) UINT32 __cdecl sbrec_test_configure(UINT32 value, const CHAR* path) {
    if(value<1 || value>13 || !path || strlen(path)>=sizeof(output)) return 1;
    AcquireSRWLockExclusive(&stateLock);
    mode=value; strcpy(output,path); keyHead=keyCount=0; totalBytes=0; reconnectOwner=NULL; memset(attemptUploads,0,sizeof(attemptUploads));
    InterlockedExchange(&calls,0); InterlockedExchange(&mediaCalls,0); InterlockedExchange(&parsedAcks,0);
    ReleaseSRWLockExclusive(&stateLock); return 0;
}
__declspec(dllexport) UINT32 __cdecl sbrec_test_counts(UINT32* api,UINT32* media,UINT32* acks,UINT64* bytes) {
    *api=(UINT32)calls; *media=(UINT32)mediaCalls; *acks=(UINT32)parsedAcks; *bytes=totalBytes; return 0;
}
void sbrec_test_frame(UINT64 pts,UINT32 track,UINT32 flags) {
    if(track!=1 || !(flags&1)) return;
    AcquireSRWLockExclusive(&stateLock);
    if(keyCount<256) keys[(keyHead+keyCount++)%256]=pts/10000;
    ReleaseSRWLockExclusive(&stateLock);
}
static void ack(PCurlResponse response,BOOL final) {
    UINT64 timecode=0; CHAR json[512]; int n;
    // D3 paired controls intentionally inject neither Received nor Persisted.
    // Their intercepted local delivery never represents service persistence.
    if(mode>=8) return;
    AcquireSRWLockExclusive(&stateLock);
    if(keyCount>(final ? 0u : 1u)) { timecode=keys[keyHead]; keyHead=(keyHead+1)%256; --keyCount; }
    ReleaseSRWLockExclusive(&stateLock);
    if(!timecode) return;
    n=snprintf(json,sizeof(json),"{\"EventType\":\"RECEIVED\",\"FragmentTimecode\":%llu,\"FragmentNumber\":\"10000000000000000000000000000000000000000000000%llu\"}\n",timecode,timecode);
    if(postWriteCallback(json,1,n,response->pCurlRequest)==n) InterlockedIncrement(&parsedAcks);
    // Mode 4 withholds Persisted deliberately: Received is not durability.
    if(mode!=4) {
        n=snprintf(json,sizeof(json),"{\"EventType\":\"PERSISTED\",\"FragmentTimecode\":%llu,\"FragmentNumber\":\"10000000000000000000000000000000000000000000000%llu\"}\n",timecode,timecode);
        if(postWriteCallback(json,1,n,response->pCurlRequest)==n) InterlockedIncrement(&parsedAcks);
    }
}
// D2 only: delay SDK reads, not generated encoding. This models backpressure on
// one open upload, not TCP reconnection, TLS bytes or real service durability.
// Every observation is a bounded finite event; no URLs/headers/credentials enter it.
static CURLcode stalled_perform(PCurlResponse response, UINT32 requestNumber) {
    CHAR bytes[16384], tracePath[2140],mediaPath[2100]; FILE *file=NULL,*trace=NULL;
    UINT64 attemptBytes=0;BOOL reconnect=mode>=11;
    LARGE_INTEGER origin,now,frequency; UINT32 rows=0; BOOL stalled=FALSE,resumed=FALSE;
    CURLcode result=CURLE_OK;
    // Never overwrite/concatenate MKV on an SDK retry. Multiple connections are
    // explicitly unsupported by this single-upload fixture and visible in counts.
    if((!reconnect&&requestNumber!=1)||requestNumber>8) return CURLE_ABORTED_BY_CALLBACK;
    if(reconnect) {
        snprintf(mediaPath,sizeof(mediaPath),"%s.attempt-%03u.mkv",output,requestNumber);
        AcquireSRWLockExclusive(&stateLock);
        if(!reconnectOwner)reconnectOwner=response->pCurlRequest->pCurlApiCallbacks;
        attemptUploads[requestNumber-1]=response->pCurlRequest->uploadHandle;
        if(reconnectOwner!=response->pCurlRequest->pCurlApiCallbacks){ReleaseSRWLockExclusive(&stateLock);return CURLE_WRITE_ERROR;}
        ReleaseSRWLockExclusive(&stateLock);
    } else strcpy(mediaPath,output);
    snprintf(tracePath,sizeof(tracePath),"%s.transport.jsonl",mediaPath);
    file=fopen(mediaPath,"wbx"); trace=fopen(tracePath,"wbx");
    if(!file||!trace) {if(file)fclose(file);if(trace)fclose(trace);return CURLE_WRITE_ERROR;}
    if(response->pCurlRequest->pCurlApiCallbacks->curlReadCallbackHookFn &&
       (!reconnect||response->pCurlRequest->pCurlApiCallbacks->curlReadCallbackHookFn!=observe_read)) {fclose(file);fclose(trace);return CURLE_WRITE_ERROR;}
    response->pCurlRequest->pCurlApiCallbacks->curlReadCallbackHookFn=observe_read;
    if(!reconnect){AcquireSRWLockExclusive(&stateLock);senderResponse=response;ReleaseSRWLockExclusive(&stateLock);}
    observedReadStatus=(STATUS)0xffffffff;observedReadBytes=observedReadCalls=0;
    QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&origin);
#define TRACE(event,n) do { QueryPerformanceCounter(&now); \
    if(++rows>4096){result=CURLE_WRITE_ERROR;goto finished;} \
    fprintf(trace,"{\"event\":\"%s\",\"qpc\":%lld,\"frequency\":%lld,\"bytes\":%llu,\"total\":%llu,\"sdkStatus\":%u,\"sdkBytes\":%u,\"sdkCalls\":%u}\n",event,now.QuadPart,frequency.QuadPart,(UINT64)(n),totalBytes,observedReadStatus,observedReadBytes,observedReadCalls); \
    } while(0)
    if(reconnect) {
        SbrecSenderPacer* pacer=&response->pCurlRequest->pCurlApiCallbacks->senderPacer;
        AcquireSRWLockShared(&pacer->lock);
        fprintf(trace,"{\"event\":\"attempt\",\"attempt\":%u,\"uploadHandle\":%llu,\"owner\":1,\"usedAtBegin\":%llu,\"creditAtBegin\":%u,\"reservedAtBegin\":%u}\n",requestNumber,response->pCurlRequest->uploadHandle,pacer->used,pacer->credit,pacer->reserved);
        ReleaseSRWLockShared(&pacer->lock);
    }
    TRACE("begin",0);
    while(!ATOMIC_LOAD_BOOL(&response->pCurlRequest->requestInfo.terminating)) {
        double elapsed; SIZE_T n;
        QueryPerformanceCounter(&now); elapsed=(double)(now.QuadPart-origin.QuadPart)/frequency.QuadPart;
        if(elapsed>=15) {TRACE("transportDeadline",0);result=CURLE_OPERATION_TIMEDOUT;break;}
        if((mode==6 || mode==7 || mode==9) && elapsed>=0.75 && !resumed) {
            if(!stalled) {stalled=TRUE;TRACE("stall",0);}
            if(elapsed<2.75) {Sleep(5);continue;}
            if(mode==7) {TRACE("injectedTimeout",0);result=CURLE_OPERATION_TIMEDOUT;break;}
            resumed=TRUE;TRACE("resume",0);
        }
        observedReadStatus=(STATUS)0xffffffff;observedReadBytes=observedReadCalls=0;
        n=postReadCallback(bytes,1,sizeof(bytes),response->pCurlRequest);
        if(n==CURL_READFUNC_ABORT) {TRACE("sdkAbort",0);result=CURLE_ABORTED_BY_CALLBACK;break;}
        if(n==CURL_READFUNC_PAUSE) {
            TRACE("sdkPause",0);
            ack(response,TRUE);ATOMIC_STORE_BOOL(&response->paused,FALSE);Sleep(10);continue;
        }
        if(!n) {ack(response,TRUE);TRACE("eos",0);break;}
        if(totalBytes+n>8ULL*1024*1024 || fwrite(bytes,1,n,file)!=n) {TRACE("outputLimitOrWrite",0);result=CURLE_WRITE_ERROR;break;}
        if(reconnect){AcquireSRWLockExclusive(&stateLock);totalBytes+=n;ReleaseSRWLockExclusive(&stateLock);}
        else totalBytes+=n;
        attemptBytes+=n;TRACE("read",n);ack(response,FALSE);
        // D8 returns a real retryable curl timeout; the maintained completion and
        // stream-terminated path alone may initiate another PutMedia request.
        if((mode==12&&requestNumber==1&&attemptBytes>=65536)||(mode==13&&attemptBytes>=16384)) {
            response->callInfo.httpStatus=0;interceptedHttpStatus=0;
            TRACE("injectedDisconnect",0);result=CURLE_OPERATION_TIMEDOUT;break;
        }
    }
finished:
    if(!reconnect){AcquireSRWLockExclusive(&stateLock);senderResponse=NULL;ReleaseSRWLockExclusive(&stateLock);}
    fprintf(trace,"{\"event\":\"pacer\",\"used\":%llu,\"waits\":%lld,\"cancelled\":%lld,\"timeouts\":%lld,\"foreignReadRejected\":%ld,\"rateBytesPerSecond\":%u,\"creditCapBytes\":%u,\"quantumBytes\":%u}\n",
        response->pCurlRequest->pCurlApiCallbacks->senderPacer.used,
        response->pCurlRequest->pCurlApiCallbacks->senderPacer.waits,
        response->pCurlRequest->pCurlApiCallbacks->senderPacer.cancelled,
        response->pCurlRequest->pCurlApiCallbacks->senderPacer.timeouts,foreignReadRejected,
        response->pCurlRequest->pCurlApiCallbacks->senderPacer.config.bytesPerSecond,
        response->pCurlRequest->pCurlApiCallbacks->senderPacer.config.capacityBytes,
        response->pCurlRequest->pCurlApiCallbacks->senderPacer.config.readBytes);
    if(!reconnect)response->pCurlRequest->pCurlApiCallbacks->curlReadCallbackHookFn=NULL;
    fprintf(trace,"{\"event\":\"end\",\"curlCode\":%u,\"rows\":%u}\n",(UINT32)result,rows);
    fclose(file);fclose(trace);
#undef TRACE
    return result;
}
static CURLcode runner_perform(PCurlResponse response) {
    PCurlRequest request=response->pCurlRequest; CHAR bytes[16384]; const CHAR* json;
    FILE* file=NULL; UINT64 deadline=GETTIME()+30*HUNDREDS_OF_NANOS_IN_A_SECOND;
    interceptedHttpStatus=200;
    LONG callNumber=InterlockedIncrement(&calls);
    if(!mode) return CURLE_ABORTED_BY_CALLBACK; // interception must be explicitly armed
    if(mode==2 || mode==3) {
        if(mode==3) while(!ATOMIC_LOAD_BOOL(&request->requestInfo.terminating) && GETTIME()<deadline) THREAD_SLEEP(10*HUNDREDS_OF_NANOS_IN_A_MILLISECOND);
        return CURLE_COULDNT_CONNECT;
    }
    if(!request->streaming) {
        if(setupDelayMs) {
            UINT64 available,begin=GETTIME(),limit=request->requestInfo.completionTimeout;
            CURLcode outcome=CURLE_OK;
            AcquireSRWLockExclusive(&stateLock);
            if(!setupAvailableAt)setupAvailableAt=GETTIME()+(UINT64)setupDelayMs*HUNDREDS_OF_NANOS_IN_A_MILLISECOND;
            available=setupAvailableAt;ReleaseSRWLockExclusive(&stateLock);
            // curl_easy_perform is intercepted, so explicitly enforce the SAME
            // per-request CURLOPT_TIMEOUT_MS bound Response.c would give curl.
            // Only the finite setup outage spans retries; no single request may
            // magically succeed after its maintained completion timeout.
            while(GETTIME()<available && !ATOMIC_LOAD_BOOL(&request->requestInfo.terminating)
                  && (limit==SERVICE_CALL_INFINITE_TIMEOUT || GETTIME()-begin<limit))
                THREAD_SLEEP(10*HUNDREDS_OF_NANOS_IN_A_MILLISECOND);
            if(ATOMIC_LOAD_BOOL(&request->requestInfo.terminating))outcome=CURLE_ABORTED_BY_CALLBACK;
            else if(limit!=SERVICE_CALL_INFINITE_TIMEOUT && GETTIME()-begin>=limit)outcome=CURLE_OPERATION_TIMEDOUT;
            if(callNumber<=16)printf("{\"stage\":\"offline-setup-response\",\"call\":%ld,\"completionLimitMs\":%llu,\"elapsedMs\":%llu,\"curlCode\":%u}\n",callNumber,limit/HUNDREDS_OF_NANOS_IN_A_MILLISECOND,(GETTIME()-begin)/HUNDREDS_OF_NANOS_IN_A_MILLISECOND,(UINT32)outcome);
            if(outcome!=CURLE_OK)return outcome;
        }
        if(strstr(request->requestInfo.url,"describeStream")) {
            json="{\"StreamInfo\":{\"StreamName\":\"sbrec-offline\",\"StreamARN\":\"arn:aws:kinesisvideo:us-east-2:000000000000:stream/sbrec-offline/1\",\"Status\":\"ACTIVE\",\"MediaType\":\"video/h264,audio/aac\",\"DataRetentionInHours\":2,\"Version\":\"1\"}}";
        } else if(strstr(request->requestInfo.url,"getDataEndpoint")) {
            json="{\"DataEndpoint\":\"https://intercepted.invalid\"}";
        } else return CURLE_ABORTED_BY_CALLBACK; // no unrecognized endpoint fallback
        postResponseWriteCallback((PCHAR)json,1,strlen(json),request); return CURLE_OK;
    }
    { UINT32 requestNumber=(UINT32)InterlockedIncrement(&mediaCalls);
      if(mode>=5) return stalled_perform(response,requestNumber); }
    // One SDK stream in this harness. CREATE_NEW-like test output policy is
    // enforced by the caller; never contains request headers or credentials.
    file=fopen(output,"wb"); if(!file) return CURLE_WRITE_ERROR;
    while(GETTIME()<deadline && !ATOMIC_LOAD_BOOL(&request->requestInfo.terminating)) {
        SIZE_T n=postReadCallback(bytes,1,sizeof(bytes),request);
        if(n==CURL_READFUNC_ABORT) break;
        if(n==CURL_READFUNC_PAUSE) {
            ack(response,TRUE); ATOMIC_STORE_BOOL(&response->paused,FALSE);
            THREAD_SLEEP(10*HUNDREDS_OF_NANOS_IN_A_MILLISECOND); continue;
        }
        if(n) {
            if(totalBytes+n>64ULL*1024*1024 || fwrite(bytes,1,n,file)!=n) { fclose(file); return CURLE_WRITE_ERROR; }
            totalBytes+=n; ack(response,FALSE);
        }
        if(!n) { ack(response,TRUE); break; }
    }
    fclose(file); return CURLE_OK;
}
static CURLcode runner_getinfo(CURL* curl,CURLINFO info,...) {
    va_list args; va_start(args,info);
    if(info==CURLINFO_RESPONSE_CODE) *va_arg(args,long*)=interceptedHttpStatus;
    else if(info==CURLINFO_EFFECTIVE_URL) *va_arg(args,char**)="https://intercepted.invalid";
    else if(info==CURLINFO_SPEED_UPLOAD_T || info==CURLINFO_SIZE_UPLOAD_T) *va_arg(args,curl_off_t*)=0;
    else *va_arg(args,double*)=0;
    va_end(args); return CURLE_OK;
}

// Observe actual maintained completion information after Response.c maps curl's
// failure. This records facts only; no retry/result/ACK is manufactured here.
STATUS curlCompleteSync(PCurlResponse response) {
    STATUS status=observedCurlCompleteSync(response);
    if(mode>=11 && response->pCurlRequest->streaming) {
        UINT32 index=0;CHAR path[2140];FILE* trace;LARGE_INTEGER now;
        AcquireSRWLockShared(&stateLock);
        for(index=0;index<8;index++)if(attemptUploads[index]==response->pCurlRequest->uploadHandle)break;
        ReleaseSRWLockShared(&stateLock);
        if(index<8) {
            snprintf(path,sizeof(path),"%s.attempt-%03u.mkv.transport.jsonl",output,index+1);
            trace=fopen(path,"ab");QueryPerformanceCounter(&now);
            if(trace){fprintf(trace,"{\"event\":\"completion\",\"qpc\":%lld,\"uploadHandle\":%llu,\"httpStatus\":%u,\"callResult\":%u,\"status\":%u,\"terminating\":%u,\"endOfStream\":%u}\n",now.QuadPart,response->pCurlRequest->uploadHandle,response->callInfo.httpStatus,response->callInfo.callResult,status,ATOMIC_LOAD_BOOL(&response->pCurlRequest->requestInfo.terminating),response->endOfStream);fclose(trace);}
        }
    }
    return status;
}

// D8 test-only alias at the existing CurlApiCallbacks call site. Call PIC's
// maintained termination/recovery entry unchanged and record its actual return.
STATUS sbrec_test_stream_terminated(STREAM_HANDLE stream,UPLOAD_HANDLE upload,SERVICE_CALL_RESULT result) {
    STATUS status=kinesisVideoStreamTerminated(stream,upload,result);
    if(mode>=11) {
        UINT32 index;CHAR path[2140];FILE* trace;LARGE_INTEGER now;
        AcquireSRWLockShared(&stateLock);
        for(index=0;index<8;index++)if(attemptUploads[index]==upload)break;
        ReleaseSRWLockShared(&stateLock);
        if(index<8){snprintf(path,sizeof(path),"%s.attempt-%03u.mkv.transport.jsonl",output,index+1);trace=fopen(path,"ab");QueryPerformanceCounter(&now);
            if(trace){fprintf(trace,"{\"event\":\"streamTerminated\",\"qpc\":%lld,\"uploadHandle\":%llu,\"callResult\":%u,\"status\":%u}\n",now.QuadPart,upload,result,status);fclose(trace);}}
    }
    return status;
}
