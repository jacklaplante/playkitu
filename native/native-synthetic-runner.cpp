// Generated/external asynchronous harness. kvssink/Producer SDK owns encoding input
// packaging, signing, refresh callbacks, MKV, transport, retries and ACK parsing.
#include "native-synthetic-runner.h"
#include "native-synthetic-media.h"
#include "native-media-clock.h"
#include <windows.h>
#include <array>
#include <chrono>
#include <condition_variable>
#include <cctype>
#include <cstring>
#include <filesystem>
#include <memory>
#include <mutex>
#include <thread>
#include "native-region.h"
#include <cmath>
#include <gst/app/gstappsrc.h>

#ifdef SBREC_RUNNER_LIFECYCLE_TEST
// Source-included unit regression only; absent from the shipped DLL. Allows the
// exact retained-find / release / start interleaving without scheduler luck.
void sbrec_test_after_find() noexcept;
#endif
#ifdef SBREC_RUNNER_CLOCK_TEST
void sbrec_test_source_delay(uint64_t,GstPad*,GstBuffer*) noexcept;
#endif

namespace {
using Steady = std::chrono::steady_clock;
constexpr uint64_t Unknown = GST_CLOCK_TIME_NONE;
SbrecClock clock_now() noexcept {
    LARGE_INTEGER a,b,f; FILETIME ft;
    QueryPerformanceCounter(&a); GetSystemTimePreciseAsFileTime(&ft); QueryPerformanceCounter(&b); QueryPerformanceFrequency(&f);
    ULARGE_INTEGER utc; utc.LowPart=ft.dwLowDateTime; utc.HighPart=ft.dwHighDateTime;
    return {static_cast<uint64_t>(a.QuadPart+(b.QuadPart-a.QuadPart)/2),static_cast<uint64_t>(f.QuadPart),
        utc.QuadPart-116444736000000000ULL,static_cast<uint64_t>((b.QuadPart-a.QuadPart)*10000000/f.QuadPart+1)};
}
struct Credentials {
    std::array<char,128> access{}, secret{}; std::array<char,2048> token{};
    uint32_t a=0,s=0,t=0; uint64_t expiry=0;
    ~Credentials() { clear(); }
    void clear() noexcept { SecureZeroMemory(this,sizeof(*this)); }
    bool copy(const SbrecCredentialUpdate* x) noexcept {
        if (!x || !x->accessKey || !x->secretKey || !x->sessionToken || x->accessKeyBytes<16 || x->accessKeyBytes>access.size() ||
            x->secretKeyBytes<16 || x->secretKeyBytes>secret.size() || !x->sessionTokenBytes || x->sessionTokenBytes>token.size()) return false;
        clear(); a=x->accessKeyBytes; s=x->secretKeyBytes; t=x->sessionTokenBytes; expiry=x->expiresUnixSeconds;
        memcpy(access.data(),x->accessKey,a); memcpy(secret.data(),x->secretKey,s); memcpy(token.data(),x->sessionToken,t); return true;
    }
    SbrecCredentialUpdate view() const noexcept { return {access.data(),a,secret.data(),s,token.data(),t,expiry}; }
};
struct Run {
    std::mutex mutex; std::condition_variable wake;
    SbrecRunSettings settings{}; SbrecRunStatus status{};
    char awsRegion[32]{}; // Owned immutable copy of PLAYREEL_AWS_REGION, not an ambient SDK default.
    std::array<SbrecObservation,1024> ring{}; uint32_t head=0,count=0;
    Credentials pending; bool hasPending=false; uint64_t revision=0;
    std::atomic<bool> stop{false}, done{false};
    std::atomic<bool> external{false};
    // The input gate never waits: submit uses try_lock on registry and this
    // gate, then only bounded copies/pool admission/appsrc block=false. Worker
    // configuration/teardown owns these refs; no input reaches freed GObjects.
    std::mutex inputMutex;
    std::atomic<bool> inputReady{false};
    GstElement* inputVideo=nullptr; GstElement* inputAudio=nullptr;
    SbrecMediaConfigV1 mediaConfig{}; bool mediaConfigured=false;
    GstBufferPool* videoPool=nullptr; GstBufferPool* audioPool=nullptr;
    SbrecExternalStatusV1 inputStatus{};
    std::array<SbrecExternalObservationV1,64> inputEvents{};
    uint32_t inputHead=0,inputCount=0;
    uint64_t inputSequence=0,lastInputPts[2]{Unknown,Unknown},lastInputDuration[2]{};
    bool inputGap[2]{};
    std::thread worker, timer;
    Steady::time_point started;
    Steady::time_point inputReadyAt;
    bool readinessPublished=false; // mutex-protected; one immutable startup epoch
    GstElement* counter=nullptr; // worker-owned, probes quiesced before unref
    SbrecSdkObserver observer{};
    Samples video{"video"},audio{"audio"};
    std::unique_ptr<media_clock::Engine> clocks;
    uint64_t provider=0, previousSourcePts=Unknown;
    using Create=uint32_t(__cdecl*)(uint64_t*) noexcept;
    using Update=uint32_t(__cdecl*)(uint64_t,const SbrecCredentialUpdate*) noexcept;
    using Release=uint32_t(__cdecl*)(uint64_t) noexcept;
    using Bind=uint32_t(__cdecl*)(GstElement*,uint64_t) noexcept;
    using Retire=uint32_t(__cdecl*)(GstElement*) noexcept;
    Update update=nullptr; Release release=nullptr;
    Retire retire=nullptr;
    Run() { status.size=sizeof(status); status.firstVideoRunningNs=status.firstAudioRunningNs=Unknown; }
    ~Run() { if(worker.joinable()) worker.join(); if(timer.joinable()) timer.join(); }
    void retire_created() noexcept {
        // Caller holds mutex. A start may already retain this Run from find();
        // fence it before unregistering, while no worker/callback exists yet.
        stop=true; status.state=RUN_TERMINAL; status.reason=RUN_CANCELLED;
        status.workerDone=status.callbacksQuiescent=1; done=true;
    }
    void emit(SbrecObservation event) noexcept {
        event.sequence=++status.observations; event.epoch=status.resets;
        if(count==settings.observationCapacity) { ++status.droppedObservations; status.degraded=1; return; }
        ring[(head+count)%settings.observationCapacity]=event; ++count;
    }
    void change(uint32_t state,uint32_t reason=RUN_NONE) noexcept {
        std::lock_guard<std::mutex> lock(mutex);
        status.state=state; if(reason && !status.reason) status.reason=reason;
        SbrecObservation e{}; e.kind=OBS_STATE; e.result=status.reason; e.flags=state; e.clock=clock_now(); emit(e);
    }
    void halt_locked(uint32_t reason) noexcept {
        stop=true;
        if(!status.reason) status.reason=reason;
        if(status.state!=RUN_TERMINAL) status.state=RUN_STOPPING;
        wake.notify_all();
    }
    void halt(uint32_t reason) noexcept {
        std::lock_guard<std::mutex> lock(mutex); halt_locked(reason);
    }
    bool startup_expired(Steady::time_point now) const noexcept {
        // D19 external-input policy: setup and first SDK acceptance are different
        // phases. Neither retries nor credentials reset these fixed deadlines.
        if(!external)return now-started>=std::chrono::milliseconds(settings.startupMs);
        if(now-started>=std::chrono::seconds(30))return true;
        return readinessPublished ? now-inputReadyAt>=std::chrono::milliseconds(settings.startupMs)
            : now-started>=std::chrono::seconds(20);
    }
    void startup_locked(uint32_t phase) noexcept {
        SbrecObservation e{};e.kind=OBS_STARTUP;e.flags=phase;e.clock=clock_now();
        e.result=static_cast<uint32_t>(std::chrono::duration_cast<std::chrono::milliseconds>(Steady::now()-started).count());emit(e);
    }
    void startup(uint32_t phase) noexcept {
        std::lock_guard<std::mutex> lock(mutex);startup_locked(phase);
    }
    uint32_t publish_input_ready() noexcept {
        std::scoped_lock lock(mutex,inputMutex);
        auto now=Steady::now();
        if(stop)return RUN_CANCELLED;
        if(readinessPublished)return RUN_BUSY;
        if(startup_expired(now)){halt_locked(RUN_START_TIMEOUT);return RUN_START_TIMEOUT;}
        auto sample=clock_now();inputReadyAt=now;readinessPublished=true;
        inputStatus.originQpc=sample.qpc;inputStatus.frequency=sample.frequency;
        inputReady=true;startup_locked(STARTUP_INPUT_READY);return RUN_OK;
    }
};
std::mutex registryMutex;
std::shared_ptr<Run> active;
uint64_t generation=0;
std::atomic<uint64_t> inputContentions{0}; // process-wide, including registry contention before a run can be identified
std::shared_ptr<Run> find(uint64_t handle) {
    std::lock_guard<std::mutex> lock(registryMutex); return handle && handle==generation ? active : nullptr;
}
bool text(const char* x,size_t n) { return x[0] && memchr(x,0,n); }
bool uuid(const char* x) {
    if(strnlen(x,37)!=36) return false;
    for(size_t i=0;i<36;i++) if(i==8||i==13||i==18||i==23) { if(x[i]!='-') return false; }
        else if(!isxdigit(static_cast<unsigned char>(x[i]))) return false;
    return true;
}
bool valid(const SbrecRunSettings* s,bool external=false) {
    if(!s || s->size!=sizeof(*s) || s->seconds<1 || s->seconds>(external ? 14400u : 180u) || s->width<64 || s->width>1280 || s->height<64 || s->height>720 ||
       s->width%2 || s->height%2 || !s->fps || s->fps>30 || !s->observationCapacity || s->observationCapacity>1024 ||
       s->startupMs<100 || s->startupMs>10000 || s->flushMs<100 || s->flushMs>5000 || !s->encodedByteLimit || s->encodedByteLimit>(external ? 4ULL*1024*1024*1024 : 64ULL*1024*1024) ||
       !uuid(s->sessionId) || !uuid(s->runId) || !text(s->streamName,sizeof(s->streamName)) || !text(s->caFile,sizeof(s->caFile)) ||
       !s->managedFrequency || !s->correspondenceQpc) return false;
    for(const char* c=s->streamName;*c;c++) if(!isalnum(static_cast<unsigned char>(*c)) && *c!='_' && *c!='-' && *c!='.') return false;
    return std::filesystem::u8path(s->caFile).is_absolute();
}
void sdk_event(void* context,const SbrecSdkEvent* x) noexcept {
    auto& r=*static_cast<Run*>(context);
    std::lock_guard<std::mutex> lock(r.mutex);
    SbrecObservation e{}; e.kind=x->kind; e.track=x->track; e.flags=x->flags; e.result=x->result;
    e.clock=clock_now(); e.runningNs=x->inputRunningNs; e.sdkPts100ns=x->sdkPts100ns;
    // Submitted SDK timestamps include a downward UTC-grid translation of <1ms.
    // Carry its conservative bound in the existing UTC calibration metadata;
    // source/DSP clock samples and audio-clock tolerances are unchanged.
    if(x->kind==SBREC_FRAME) e.clock.uncertainty100ns = e.clock.uncertainty100ns > UINT64_MAX-SbrecSdkUtcQuantization100ns
        ? UINT64_MAX : e.clock.uncertainty100ns+SbrecSdkUtcQuantization100ns;
    e.upload=x->upload; e.timecodeMs=x->timecodeMs; e.bytes=x->bytes; memcpy(e.fragment,x->fragment,sizeof(e.fragment));
    auto& s=r.status;
    if(x->kind==SBREC_FRAME && !x->result) {
        auto& count=x->track==1 ? s.acceptedVideo : s.acceptedAudio;
        auto& first=x->track==1 ? s.firstVideoRunningNs : s.firstAudioRunningNs;
        auto& firstSdk=x->track==1 ? s.firstVideoSdk100ns : s.firstAudioSdk100ns;
        auto& last=x->track==1 ? s.lastVideoRunningNs : s.lastAudioRunningNs;
        auto& lastSdk=x->track==1 ? s.lastVideoSdk100ns : s.lastAudioSdk100ns;
        if(!count++) { first=x->inputRunningNs; firstSdk=x->sdkPts100ns; }
        last=x->inputRunningNs; lastSdk=x->sdkPts100ns;
        if(s.state==RUN_INITIALIZING && !r.stop) {
            if(r.startup_expired(Steady::now()))r.halt_locked(RUN_START_TIMEOUT);
            else {s.state=RUN_SENDING;r.startup_locked(STARTUP_FIRST_SDK_FRAME);}
        }
    } else if(x->kind==SBREC_FRAME && x->result) {
        s.degraded=1; ++s.gaps;
    } else if(x->kind==SBREC_ACK) {
        if(x->flags==2) ++s.receivedAcks;
        if(x->flags==3) ++s.persistedAcks;
        if(x->flags==4) { s.degraded=1; ++s.gaps; }
    } else if(x->kind==SBREC_RESET) { ++s.resets; ++s.gaps; s.degraded=1; }
    else if(x->kind==SBREC_GAP) { ++s.gaps; s.degraded=1; }
    r.emit(e);
    if(r.clocks) {
        if(x->kind==SBREC_FRAME && x->track==1 && !x->result)
            r.clocks->submitted(x->inputRunningNs,x->sdkPts100ns,x->flags,s.resets,s.droppedObservations);
        else if(x->kind==SBREC_RESET)r.clocks->boundary(CLOCK_PRODUCER_RESET);
    }
}
bool cancelled(void* context) noexcept { return static_cast<Run*>(context)->stop.load(); }
GstPadProbeReturn source_probe(GstPad* pad,GstPadProbeInfo* info,gpointer data) {
    auto& r=*static_cast<Run*>(data);
    if(r.stop) return GST_PAD_PROBE_DROP;
#ifdef SBREC_RUNNER_CLOCK_TEST
    uint64_t ordinal;{std::lock_guard<std::mutex> lock(r.mutex);ordinal=r.status.sourceFrames+1;}
    sbrec_test_source_delay(ordinal,pad,GST_PAD_PROBE_INFO_BUFFER(info));
#endif
    SbrecObservation e{}; e.kind=OBS_SOURCE; e.track=1; e.clock=clock_now(); e.ptsNs=GST_BUFFER_PTS(GST_PAD_PROBE_INFO_BUFFER(info));
    auto segment=r.clocks ? media_clock::segment_at(pad,e.ptsNs) : media_clock::SegmentSample{};
    GstElement* parent=r.clocks ? gst_pad_get_parent_element(pad) : nullptr;
    uint64_t frame;bool selected=false;
    { std::lock_guard<std::mutex> lock(r.mutex); frame=++r.status.sourceFrames;
      if(e.ptsNs==Unknown || (r.previousSourcePts!=Unknown && (e.ptsNs<r.previousSourcePts || e.ptsNs-r.previousSourcePts>2*GST_SECOND/r.settings.fps))) {
          ++r.status.gaps; r.status.degraded=1; e.flags=1;
      }
      r.previousSourcePts=e.ptsNs;
      if(frame==1) r.status.firstSource=e.clock; r.status.lastSource=e.clock; r.emit(e);
      if(r.clocks) {r.clocks->set_source(parent);r.clocks->source_segment(segment);selected=r.clocks->wants(segment.running);} }
    if(selected) {
        auto sample=media_clock::sample_source(parent);
        {std::lock_guard<std::mutex> lock(r.mutex);
         r.clocks->clock_sample(sample);
         r.clocks->source(frame,e.ptsNs,GST_BUFFER_DURATION(GST_PAD_PROBE_INFO_BUFFER(info)),segment,e.clock.qpc,sample,r.status.resets,r.status.droppedObservations);}
        if(sample.identity)gst_object_unref(sample.identity);
    }
    if(parent)gst_object_unref(parent);
    char label[64]; snprintf(label,sizeof(label),"frame %llu",static_cast<unsigned long long>(frame));
    if(r.counter) g_object_set(r.counter,"text",label,nullptr);
    return r.stop ? GST_PAD_PROBE_DROP : GST_PAD_PROBE_OK;
}
struct EncodedProbe { Run* run; Samples* samples; uint32_t track; };
GstPadProbeReturn encoded_probe(GstPad* pad,GstPadProbeInfo* info,gpointer data) {
    auto& p=*static_cast<EncodedProbe*>(data); auto& r=*p.run;
    if(r.stop) return GST_PAD_PROBE_DROP;
    // Shared B0 timestamp measurement. Queue accounting bounds cumulative SDK
    // ingress (not curl bytes); SDK's bounded store also applies backpressure.
    observe(pad,info,p.samples);
    auto segment=r.clocks && p.track==1 ? media_clock::segment_at(pad,p.samples->last) : media_clock::SegmentSample{};
    SbrecObservation e{}; e.kind=OBS_ENCODED; e.track=p.track; e.clock=clock_now();
    e.ptsNs=p.samples->last; e.segmentStartNs=p.samples->segment.start; e.segmentBaseNs=p.samples->segment.base;
    e.runningNs=p.samples->lastRunning; e.bytes=gst_buffer_get_size(GST_PAD_PROBE_INFO_BUFFER(info));
    { std::lock_guard<std::mutex> lock(r.mutex);
      if(e.bytes>r.settings.encodedByteLimit-r.status.encodedBytes) {
          r.stop=true; r.status.reason=RUN_BYTE_LIMIT; r.status.state=RUN_STOPPING; return GST_PAD_PROBE_DROP;
      }
      r.status.encodedBytes+=e.bytes; r.emit(e);
      if(r.clocks && p.track==1)r.clocks->encoded(p.samples->last,segment,r.status.droppedObservations); }
    return r.stop ? GST_PAD_PROBE_DROP : GST_PAD_PROBE_OK;
}
GstPadProbeReturn audio_source_probe(GstPad*,GstPadProbeInfo*,gpointer data) {
    return static_cast<Run*>(data)->stop ? GST_PAD_PROBE_DROP : GST_PAD_PROBE_OK;
}
void apply_credentials(Run& r) {
    std::lock_guard<std::mutex> lock(r.mutex);
    if(!r.hasPending) return;
    auto view=r.pending.view(); r.status.credentialResult=r.update(r.provider,&view);
    r.status.credentialRevision=r.revision; r.pending.clear(); r.hasPending=false;
    if(r.status.credentialResult) { r.status.degraded=1; /* invalid replacement keeps prior B2a snapshot */ }
}
void worker(Run* rp) noexcept {
    auto& r=*rp; GstElement *pipeline=nullptr,*sink=nullptr; GstBus* bus=nullptr;
    EncodedProbe video{&r,&r.video,1},audio{&r,&r.audio,2};
    uint32_t reason=RUN_INIT_FAILED;
    try {
        r.startup(STARTUP_WORKER);
        if(!std::filesystem::is_regular_file(std::filesystem::u8path(r.settings.caFile))) throw static_cast<uint32_t>(RUN_INIT_FAILED);
        gst_init(nullptr,nullptr);
        r.startup(STARTUP_GST_READY);
        if(r.stop) throw static_cast<uint32_t>(RUN_CANCELLED);
        GstElementFactory* f=gst_element_factory_find("mfh264enc");
        const char* klass=f ? gst_element_factory_get_metadata(f,GST_ELEMENT_METADATA_KLASS) : nullptr;
        bool hardware=klass && strstr(klass,"Hardware"); if(f) gst_object_unref(f);
        if(!hardware) throw static_cast<uint32_t>(RUN_INIT_FAILED);
        auto description=std::string("kvssink name=kvs ")+(r.external
            ? external_media(r.settings.width,r.settings.height,r.settings.fps,"kvs.",&r.mediaConfig)
            : synthetic_media(r.settings.seconds,r.settings.width,r.settings.height,r.settings.fps,"kvs.",true,&r.mediaConfig));
        GError* error=nullptr; pipeline=gst_parse_launch(description.c_str(),&error);
        if(error) { g_error_free(error); throw static_cast<uint32_t>(RUN_INIT_FAILED); }
        if(!pipeline) throw static_cast<uint32_t>(RUN_INIT_FAILED);
        r.startup(STARTUP_PIPELINE_READY);
        sink=gst_bin_get_by_name(GST_BIN(pipeline),"kvs");
        GstPlugin* plugin=gst_plugin_feature_get_plugin(GST_PLUGIN_FEATURE(gst_element_get_factory(sink)));
        const auto modulePath=std::filesystem::u8path(gst_plugin_get_filename(plugin));
        auto module=GetModuleHandleW(modulePath.c_str()); gst_object_unref(plugin);
        if(!module) throw static_cast<uint32_t>(RUN_INIT_FAILED);
        auto observerAbi=reinterpret_cast<uint32_t(__cdecl*)() noexcept>(GetProcAddress(module,"sbrec_synthetic_observer_abi_version"));
        if(!observerAbi || observerAbi()!=1) throw static_cast<uint32_t>(RUN_INIT_FAILED);
        if(r.mediaConfigured) {
            auto version=reinterpret_cast<uint32_t(__cdecl*)() noexcept>(GetProcAddress(module,"sbrec_media_config_version"));
            if(!version||version()!=1)throw static_cast<uint32_t>(RUN_INIT_FAILED);
        }
        auto create=reinterpret_cast<Run::Create>(GetProcAddress(module,"sbrec_credentials_create"));
        r.update=reinterpret_cast<Run::Update>(GetProcAddress(module,"sbrec_credentials_update"));
        r.release=reinterpret_cast<Run::Release>(GetProcAddress(module,"sbrec_credentials_release"));
        auto bind=reinterpret_cast<Run::Bind>(GetProcAddress(module,"sbrec_credentials_bind"));
        r.retire=reinterpret_cast<Run::Retire>(GetProcAddress(module,"sbrec_sink_retire"));
        if(!create||!r.update||!r.release||!bind||!r.retire||create(&r.provider)) throw static_cast<uint32_t>(RUN_INIT_FAILED);
        apply_credentials(r);
        { std::lock_guard<std::mutex> lock(r.mutex); if(r.status.credentialResult) throw static_cast<uint32_t>(RUN_CREDENTIAL_FAILED); }
        if(bind(sink,r.provider)) throw static_cast<uint32_t>(RUN_CREDENTIAL_FAILED);
        r.observer={sdk_event,cancelled,&r}; g_object_set_data(G_OBJECT(sink),"sbrec-observer-v1",&r.observer);
        g_object_set_data(G_OBJECT(sink),"sbrec-ca-path-v1",r.settings.caFile);
        g_object_set_data(G_OBJECT(sink),"sbrec-media-config-v1",&r.mediaConfig);
        // D9: let the maintained SDK recover transport failures on this same
        // stream/owner; its sender bucket and C7 retirement lifetime are retained.
        g_object_set(sink,"stream-name",r.settings.streamName,"aws-region",r.awsRegion,
            "storage-size",64u,"stop-stream-timeout",1u,"connection-timeout",1u,"completion-timeout",2u,
            "restart-on-error",TRUE,"fragment-acks",TRUE,"absolute-fragment-times",TRUE,
            "key-frame-fragmentation",TRUE,"use-original-pts",FALSE,
            "timecode-scale",SbrecSdkTimecodeScaleMs,"fragment-duration",2000u,"frame-timecodes",TRUE,nullptr);
        r.counter=gst_bin_get_by_name(GST_BIN(pipeline),"counter");
        if(r.external) {
            // Fixed pools preallocate on this worker, never on the audio callback.
            // Pool DONTWAIT plus appsrc leaky bounded queues prevents encoder/SDK
            // backpressure from allocating more raw buffers or blocking callers.
            std::lock_guard<std::mutex> inputLock(r.inputMutex);
            r.inputVideo=gst_bin_get_by_name(GST_BIN(pipeline),"sourcevideo");
            r.inputAudio=gst_bin_get_by_name(GST_BIN(pipeline),"sourceaudio");
            for(uint32_t track=0;track<2;track++) {
                auto*& pool=track ? r.audioPool : r.videoPool;
                pool=gst_buffer_pool_new(); auto* config=gst_buffer_pool_get_config(pool);
                gst_buffer_pool_config_set_params(config,nullptr,track ? 4800u*2*4 : r.settings.width*r.settings.height*4,track ? 16 : 3,track ? 16 : 3);
                if(!gst_buffer_pool_set_config(pool,config) || !gst_buffer_pool_set_active(pool,TRUE)) throw static_cast<uint32_t>(RUN_INIT_FAILED);
            }
        }
        for(const char* name : {"sourcevideo","sourceaudio","video","audio"}) {
            auto* e=gst_bin_get_by_name(GST_BIN(pipeline),name); auto* pad=gst_element_get_static_pad(e,"src");
            GstPadProbeCallback cb=source_probe; void* context=&r;
            if(!strcmp(name,"sourceaudio")) cb=audio_source_probe;
            if(!strcmp(name,"video")) { cb=encoded_probe; context=&video; }
            if(!strcmp(name,"audio")) { cb=encoded_probe; context=&audio; }
            gst_pad_add_probe(pad,GST_PAD_PROBE_TYPE_BUFFER,cb,context,nullptr); gst_object_unref(pad); gst_object_unref(e);
        }
        bus=gst_element_get_bus(pipeline);
        if(r.stop) throw static_cast<uint32_t>(RUN_CANCELLED);
        r.startup(STARTUP_PLAYING_ENTER);
        if(gst_element_set_state(pipeline,GST_STATE_PLAYING)==GST_STATE_CHANGE_FAILURE) throw static_cast<uint32_t>(RUN_INIT_FAILED);
        r.startup(STARTUP_PLAYING_RETURN);
        if(r.external) {
            // Publish once under the same cancellation/deadline fence as timer
            // expiry. A delayed successful PLAYING return must not resurrect input.
            auto result=r.publish_input_ready();if(result!=RUN_OK)throw result;
        }
        bool eosSent=false; auto flush=Steady::time_point::max();
        reason=RUN_COMPLETE;
        while(true) {
            if(!r.stop) apply_credentials(r);
            else if(!eosSent) {
                eosSent=true; flush=Steady::now()+std::chrono::milliseconds(r.settings.flushMs);
                if(r.external) { gst_app_src_end_of_stream(GST_APP_SRC(r.inputVideo)); gst_app_src_end_of_stream(GST_APP_SRC(r.inputAudio)); }
                else gst_element_send_event(pipeline,gst_event_new_eos());
            }
            auto* message=gst_bus_timed_pop_filtered(bus,20*GST_MSECOND,static_cast<GstMessageType>(GST_MESSAGE_ERROR|GST_MESSAGE_EOS));
            if(message) { bool error=GST_MESSAGE_TYPE(message)==GST_MESSAGE_ERROR; gst_message_unref(message); if(error) reason=RUN_MEDIA_FAILED; break; }
            if(eosSent && Steady::now()>=flush) { reason=RUN_FLUSH_TIMEOUT; break; }
        }
    } catch(uint32_t code) { reason=code; } catch(...) { reason=RUN_INIT_FAILED; }
    // This may block inside maintained SDK stopSync/client teardown. Never free
    // callback context or force-kill a native thread. Dedicated host watchdog is
    // mandatory; deadline state alone cannot prove bounded DLL unload latency.
    r.stop=true; r.inputReady=false; r.change(RUN_STOPPING);
    // Wait only on this worker for an in-flight bounded input copy to finish.
    // The callback submit path itself never waits on encoder/network teardown.
    {std::lock_guard<std::mutex> inputLock(r.inputMutex);}
    auto nullResult=pipeline ? gst_element_set_state(pipeline,GST_STATE_NULL) : GST_STATE_CHANGE_SUCCESS;
    // C7: NULL/unref alone did not retire kvssink's cyclic SDK ownership. Its
    // explicit retirement completes while our strong sink ref, observer and Run
    // are still valid, including failed/partial pipeline initialization.
    const bool retired=!sink || (r.retire && r.retire(sink)==0);
    if(nullResult==GST_STATE_CHANGE_FAILURE || !retired) {
        // Retain the Run and every native reference. Worker exit is NOT callback
        // quiescence; no caller may unload the module merely because done is true.
        {std::lock_guard<std::mutex> lock(r.mutex);r.status.workerDone=1;r.status.callbacksQuiescent=0;}
        r.change(RUN_TERMINAL,RUN_RETIRE_FAILED);r.done=true;r.wake.notify_all();return;
    }
    if(bus) gst_object_unref(bus);
    if(r.counter) { gst_object_unref(r.counter); r.counter=nullptr; }
    if(sink) gst_object_unref(sink);
    if(pipeline) gst_object_unref(pipeline);
    for(auto* element : {r.inputVideo,r.inputAudio}) if(element) gst_object_unref(element);
    for(auto* pool : {r.videoPool,r.audioPool}) if(pool) {gst_buffer_pool_set_active(pool,FALSE);gst_object_unref(pool);}
    r.inputVideo=r.inputAudio=nullptr; r.videoPool=r.audioPool=nullptr;
    if(r.provider && r.release) r.release(r.provider);
    { std::lock_guard<std::mutex> lock(r.mutex); r.pending.clear(); r.hasPending=false;
      if(r.clocks)r.clocks->finish();
      if((r.status.acceptedVideo || r.status.acceptedAudio) && !r.status.persistedAcks) {
          r.status.degraded=1; ++r.status.gaps;
          SbrecObservation missing{}; missing.kind=SBREC_GAP; missing.flags=2; missing.clock=clock_now();
          missing.runningNs=Unknown; r.emit(missing); // no Persisted ACK: coverage is unavailable, never invent a fragment
      }
      r.status.callbacksQuiescent=1; r.status.workerDone=1; }
    r.change(RUN_TERMINAL,reason); r.done=true; r.wake.notify_all(); sbrec_native_owned=false;
}
void timer(Run* r) noexcept {
    while(!r->done) {
        auto elapsed=std::chrono::duration_cast<std::chrono::milliseconds>(Steady::now()-r->started).count();
        { std::unique_lock<std::mutex> lock(r->mutex); r->wake.wait_for(lock,std::chrono::milliseconds(10));
          if(!r->stop && r->status.state==RUN_INITIALIZING && r->startup_expired(Steady::now()))r->halt_locked(RUN_START_TIMEOUT); }
        GstElement* source=nullptr;
        {std::lock_guard<std::mutex> lock(r->mutex);if(r->clocks && !r->stop)source=r->clocks->source_ref();}
        if(source) {
            auto sample=media_clock::sample_source(source);
            {std::lock_guard<std::mutex> lock(r->mutex);if(!r->stop)r->clocks->clock_sample(sample);}
            if(sample.identity)gst_object_unref(sample.identity);gst_object_unref(source);
        }
        if(elapsed>=r->settings.startupMs+r->settings.seconds*1000ULL) r->halt(RUN_COMPLETE);
    }
}
}
extern "C" uint32_t __cdecl sbrec_runner_abi_version() noexcept { return 1; }
#ifndef PLAYKITU_REGION_CAPABILITY_NEGATIVE
extern "C" uint32_t __cdecl sbrec_runner_trusted_region_version() noexcept { return 1; }
#endif
extern "C" uint32_t __cdecl sbrec_runner_clock(SbrecClock* out) noexcept { if(!out) return RUN_INVALID; *out=clock_now(); return RUN_OK; }
extern "C" uint32_t __cdecl sbrec_runner_create(uint64_t* out) noexcept {
    try { if(!out) return RUN_INVALID; *out=0; std::lock_guard<std::mutex> lock(registryMutex); if(active) return RUN_BUSY;
        active=std::make_shared<Run>(); *out=++generation; return RUN_OK; } catch(...) { return RUN_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_runner_start(uint64_t h,const SbrecRunSettings* settings,const SbrecCredentialUpdate* credentials) noexcept {
    try { auto r=find(h);
#ifdef SBREC_RUNNER_LIFECYCLE_TEST
        sbrec_test_after_find();
#endif
        if(!r) return RUN_INVALID;
        std::lock_guard<std::mutex> lock(r->mutex); if(!valid(settings,r->external))return RUN_INVALID;
        if(r->status.state!=RUN_CREATED || r->stop) return RUN_BUSY;
        char region[32]{};
        if(!sbrec_copy_region(region))return RUN_INVALID;
        // Cross-field GOP/FPS validation must leave CREATED runs untouched.
        // No worker exists to retire global ownership after a rejected start.
        if(r->mediaConfigured && (r->mediaConfig.gopFrames<settings->fps || r->mediaConfig.gopFrames>2*settings->fps))return RUN_INVALID;
        if(!r->pending.copy(credentials)) return RUN_INVALID;
        if(sbrec_native_owned.exchange(true)) { r->pending.clear(); return RUN_BUSY; }
        if(!r->mediaConfigured)r->mediaConfig.gopFrames=settings->fps;
        memcpy(r->awsRegion,region,sizeof(region));
        r->settings=*settings; r->hasPending=true; r->revision=1; r->status.state=RUN_INITIALIZING; r->started=Steady::now();
        try { r->timer=std::thread(timer,r.get()); r->worker=std::thread(worker,r.get()); }
        catch(...) { r->done=true; r->wake.notify_all(); sbrec_native_owned=false; r->status.state=RUN_TERMINAL; r->status.workerDone=1; r->status.callbacksQuiescent=1; throw; }
        return RUN_OK; } catch(...) { return RUN_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_runner_update_credentials(uint64_t h,const SbrecCredentialUpdate* x) noexcept {
    try { auto r=find(h); if(!r) return RUN_INVALID; std::lock_guard<std::mutex> lock(r->mutex);
        if(r->stop || r->status.state==RUN_CREATED) return RUN_NOT_READY;
        if(!r->pending.copy(x)) return RUN_INVALID; r->hasPending=true; ++r->revision; return RUN_OK; } catch(...) { return RUN_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_runner_status(uint64_t h,SbrecRunStatus* out) noexcept {
    try { auto r=find(h); if(!r || !out || out->size!=sizeof(*out)) return RUN_INVALID;
        std::lock_guard<std::mutex> lock(r->mutex); *out=r->status; return RUN_OK; } catch(...) { return RUN_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_runner_drain_observations(uint64_t h,SbrecObservation* out,uint32_t capacity,uint32_t* count) noexcept {
    try { auto r=find(h); if(!r||!out||!count||!capacity||capacity>1024) return RUN_INVALID;
        std::lock_guard<std::mutex> lock(r->mutex); *count=0;
        while(*count<capacity && r->count) { out[(*count)++]=r->ring[r->head]; r->head=(r->head+1)%r->settings.observationCapacity; --r->count; }
        return RUN_OK; } catch(...) { return RUN_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_runner_request_stop(uint64_t h) noexcept {
    try { auto r=find(h); if(!r) return RUN_INVALID;
        { std::lock_guard<std::mutex> lock(r->mutex); if(r->status.state==RUN_CREATED) { r->retire_created(); return RUN_OK; } }
        r->halt(RUN_CANCELLED); return RUN_OK; } catch(...) { return RUN_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_runner_release(uint64_t h) noexcept {
    try { std::lock_guard<std::mutex> lock(registryMutex); if(!active||h!=generation) return RUN_INVALID;
        { std::lock_guard<std::mutex> stateLock(active->mutex);
          // Lock order is registry -> Run; find() releases registry before any
          // caller takes Run. Retirement must precede dropping registration so
          // a retained start cannot launch an unregistered worker or join it.
          if(active->status.state==RUN_CREATED) active->retire_created();
          else if(!active->done || !active->status.callbacksQuiescent) return RUN_BUSY; }
        active.reset(); return RUN_OK; } catch(...) { return RUN_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_runner_clock_v2_info(uint32_t* version,uint32_t* bytes) noexcept {
    if(!version || !bytes)return RUN_INVALID;*version=2;*bytes=sizeof(FrameAnchorV2);return RUN_OK;
}
extern "C" uint32_t __cdecl sbrec_runner_enable_clock_v2(uint64_t h,uint32_t version,uint32_t bytes,uint32_t capacity,uint32_t intervalMs) noexcept {
    try {auto r=find(h);if(!r || version!=2 || bytes!=sizeof(FrameAnchorV2) || !capacity || capacity>64 || intervalMs<1000 || intervalMs>10000)return RUN_INVALID;
        std::lock_guard<std::mutex> lock(r->mutex);if(r->status.state!=RUN_CREATED || r->stop || r->external)return RUN_BUSY;
        if(r->clocks)return r->clocks->capacity==capacity && r->clocks->intervalMs==intervalMs ? RUN_OK : RUN_BUSY;
        r->clocks=std::make_unique<media_clock::Engine>(capacity,intervalMs);return RUN_OK;
    }catch(...){return RUN_INTERNAL;}
}
extern "C" uint32_t __cdecl sbrec_runner_drain_clock_v2(uint64_t h,FrameAnchorV2* out,uint32_t bytes,uint32_t capacity,uint32_t* count,uint64_t* dropped) noexcept {
    try {if(!count || !dropped)return RUN_INVALID;*count=0;
        auto r=find(h);if(!r || !out || bytes!=sizeof(FrameAnchorV2) || !capacity || capacity>64)return RUN_INVALID;
        std::lock_guard<std::mutex> lock(r->mutex);if(!r->clocks)return RUN_NOT_READY;
        *count=r->clocks->drain(out,capacity);*dropped=r->clocks->dropped();return RUN_OK;
    }catch(...){return RUN_INTERNAL;}
}
extern "C" uint32_t __cdecl sbrec_runner_external_v1_info(uint32_t* version,uint32_t* input,uint32_t* status,uint32_t* observation) noexcept {
    if(!version||!input||!status||!observation)return RUN_INVALID;
    *version=1;*input=sizeof(SbrecExternalInputV1);*status=sizeof(SbrecExternalStatusV1);*observation=sizeof(SbrecExternalObservationV1);return RUN_OK;
}
extern "C" uint32_t __cdecl sbrec_runner_enable_external_v1(uint64_t h,uint32_t version) noexcept {
    try {auto r=find(h);if(!r||version!=1)return RUN_INVALID;
        std::lock_guard<std::mutex> lock(r->mutex);if(r->status.state!=RUN_CREATED||r->stop||r->clocks)return RUN_BUSY;
        r->external=true;return RUN_OK;
    }catch(...){return RUN_INTERNAL;}
}
extern "C" uint32_t __cdecl sbrec_runner_push_external_v1(uint64_t h,const SbrecExternalInputV1* in) noexcept {
    try {
        if(!in||in->size!=sizeof(*in)||in->version!=1||in->reserved||!in->data||in->track<1||in->track>2)return RUN_INVALID;
        // Hold registry ownership throughout this short call, so release cannot
        // destroy a Run on the submit/audio thread and make that thread join it.
        std::unique_lock<std::mutex> registry(registryMutex,std::try_to_lock);
        if(!registry.owns_lock()){++inputContentions;return RUN_BUSY;}
        auto* r=active.get();if(!r||h!=generation)return RUN_INVALID;
        if(r->stop||!r->inputReady)return RUN_NOT_READY;
        std::unique_lock<std::mutex> lock(r->inputMutex,std::try_to_lock);
        if(!lock.owns_lock()){++inputContentions;return RUN_BUSY;}
        if(r->stop||!r->inputReady)return RUN_NOT_READY;
        auto now=clock_now();const uint32_t track=in->track-1;
        uint64_t duration=in->durationNs,bytes=0,pts=GST_CLOCK_TIME_NONE;
        uint32_t result=RUN_INVALID;
        bool validInput=in->captureQpc>=r->inputStatus.originQpc
            && in->captureQpc<=now.qpc+now.frequency/10
            && (in->provenance==INPUT_GENERATED || in->provenance==(track ? INPUT_AUDIO_MIX : INPUT_RENDERED));
        if(!track) {
            const uint64_t minimum=static_cast<uint64_t>(in->strideBytes)*(r->settings.height-1)+r->settings.width*4;
            validInput=validInput&&in->width==r->settings.width&&in->height==r->settings.height
                && in->strideBytes>=in->width*4&&in->strideBytes<=8192&&in->bytes>=minimum
                && in->bytes<=static_cast<uint64_t>(in->strideBytes)*in->height
                && !in->sampleFrames&&!in->channels&&!in->rate&&duration&&duration<=GST_SECOND;
            bytes=static_cast<uint64_t>(r->settings.width)*r->settings.height*4;
        } else {
            validInput=validInput&&!in->width&&!in->height&&!in->strideBytes&&!in->durationNs
                && in->channels==2&&in->rate==48000&&in->sampleFrames&&in->sampleFrames<=4800
                && reinterpret_cast<uintptr_t>(in->data)%alignof(float)==0;
            bytes=static_cast<uint64_t>(in->sampleFrames)*2*sizeof(float);
            validInput=validInput&&in->bytes==bytes;
            duration=gst_util_uint64_scale(in->sampleFrames,GST_SECOND,48000);
            if(validInput) for(uint32_t i=0;i<in->sampleFrames*2;i++) if(!std::isfinite(static_cast<const float*>(in->data)[i])){validInput=false;break;}
        }
        if(validInput) {
            pts=gst_util_uint64_scale(in->captureQpc-r->inputStatus.originQpc,GST_SECOND,r->inputStatus.frequency);
            const uint64_t maximum=static_cast<uint64_t>(r->settings.seconds)*GST_SECOND;
            const auto quantum=(GST_SECOND+r->inputStatus.frequency-1)/r->inputStatus.frequency;
            const auto lastEnd=r->lastInputPts[track]+r->lastInputDuration[track];
            // QPC quantizes a nominal frame duration (30fps cannot divide100ns
            // ticks exactly). Permit at most one clock tick of overlap; never
            // restamp input or permit a real regressing/overlapping packet.
            validInput=pts<=maximum&&duration<=maximum-pts
                && (r->lastInputPts[track]==GST_CLOCK_TIME_NONE || (pts>r->lastInputPts[track]&&(pts>=lastEnd||lastEnd-pts<=quantum)));
        }
        const bool discontinuity=r->inputGap[track] || (validInput&&r->lastInputPts[track]!=GST_CLOCK_TIME_NONE
            &&pts>r->lastInputPts[track]+r->lastInputDuration[track]+1000);
        if(validInput) {
            GstBufferPoolAcquireParams acquire{};acquire.flags=GST_BUFFER_POOL_ACQUIRE_FLAG_DONTWAIT;
            GstBuffer* buffer=nullptr;
            auto* source=GST_APP_SRC(track ? r->inputAudio : r->inputVideo);
            // Leaky appsrc otherwise reports OK after discarding an older input.
            // This is the only producer, so an explicit level check before push
            // makes overload a counted BUSY/drop; the streaming side only removes.
            if(gst_app_src_get_current_level_buffers(source)>=(track ? r->mediaConfig.inputAudio : r->mediaConfig.inputVideo)
                || gst_buffer_pool_acquire_buffer(track ? r->audioPool : r->videoPool,&buffer,&acquire)!=GST_FLOW_OK) result=RUN_BUSY;
            else {
                gst_buffer_resize(buffer,0,bytes);GstMapInfo map{};
                if(!gst_buffer_map(buffer,&map,GST_MAP_WRITE)){gst_buffer_unref(buffer);result=RUN_INTERNAL;}
                else {
                    if(track)memcpy(map.data,in->data,bytes);
                    else for(uint32_t row=0;row<in->height;row++)memcpy(map.data+row*in->width*4,
                        static_cast<const uint8_t*>(in->data)+row*in->strideBytes,in->width*4);
                    gst_buffer_unmap(buffer,&map);
                    GST_BUFFER_PTS(buffer)=pts;GST_BUFFER_DTS(buffer)=GST_CLOCK_TIME_NONE;GST_BUFFER_DURATION(buffer)=duration;
                    if(discontinuity)GST_BUFFER_FLAG_SET(buffer,GST_BUFFER_FLAG_DISCONT);
                    // Appsrc takes buffer ownership even on non-OK flow results.
                    auto flow=gst_app_src_push_buffer(source,buffer);
                    result=flow==GST_FLOW_OK ? RUN_OK : RUN_NOT_READY;
                }
            }
        }
        SbrecExternalObservationV1 event{sizeof(SbrecExternalObservationV1),1,in->track,in->provenance,result,discontinuity ? 1u : 0u,
            ++r->inputSequence,in->captureQpc,now.qpc,pts,duration,bytes};
        if(result==RUN_OK) {
            if(track)++r->inputStatus.acceptedAudio;else ++r->inputStatus.acceptedVideo;
            r->lastInputPts[track]=pts;r->lastInputDuration[track]=duration;r->inputGap[track]=false;
        } else {
            if(track)++r->inputStatus.droppedAudio;else ++r->inputStatus.droppedVideo;
            if(result==RUN_INVALID)++r->inputStatus.invalidInputs;r->inputGap[track]=true;
        }
        if(r->inputCount==r->inputEvents.size())++r->inputStatus.observationDrops;
        else {r->inputEvents[(r->inputHead+r->inputCount)%r->inputEvents.size()]=event;++r->inputCount;}
        return result;
    }catch(...){return RUN_INTERNAL;}
}
extern "C" uint32_t __cdecl sbrec_runner_external_status_v1(uint64_t h,SbrecExternalStatusV1* out) noexcept {
    try {if(!out||out->size!=sizeof(*out)||out->version!=1)return RUN_INVALID;
        std::unique_lock<std::mutex> registry(registryMutex,std::try_to_lock);if(!registry.owns_lock())return RUN_BUSY;
        auto* r=active.get();if(!r||h!=generation)return RUN_INVALID;if(!r->external)return RUN_NOT_READY;
        std::unique_lock<std::mutex> lock(r->inputMutex,std::try_to_lock);if(!lock.owns_lock())return RUN_BUSY;
        *out=r->inputStatus;out->size=sizeof(*out);out->version=1;out->ready=r->inputReady&&!r->stop;
        out->processContentionReturns=inputContentions.load();return RUN_OK;
    }catch(...){return RUN_INTERNAL;}
}
extern "C" uint32_t __cdecl sbrec_runner_drain_external_v1(uint64_t h,SbrecExternalObservationV1* out,uint32_t capacity,uint32_t* count) noexcept {
    try {if(!out||!count||!capacity||capacity>64)return RUN_INVALID;*count=0;
        std::unique_lock<std::mutex> registry(registryMutex,std::try_to_lock);if(!registry.owns_lock())return RUN_BUSY;
        auto* r=active.get();if(!r||h!=generation)return RUN_INVALID;
        std::unique_lock<std::mutex> lock(r->inputMutex,std::try_to_lock);if(!lock.owns_lock())return RUN_BUSY;
        while(*count<capacity&&r->inputCount){out[(*count)++]=r->inputEvents[r->inputHead];r->inputHead=(r->inputHead+1)%r->inputEvents.size();--r->inputCount;}
        return RUN_OK;
    }catch(...){return RUN_INTERNAL;}
}

// Validate fully before mutating Run. find retains ownership; start/configure
// serialize on Run.mutex, and the worker never sees partially applied settings.
extern "C" uint32_t __cdecl sbrec_runner_configure_media_v1(uint64_t h,const SbrecMediaConfigV1* c) noexcept {
 try {if(!c||!sbrec_valid_media(*c))return RUN_INVALID;
  auto r=find(h);if(!r)return RUN_INVALID;
  std::lock_guard<std::mutex> lock(r->mutex);
  if(r->status.state!=RUN_CREATED||r->stop)return RUN_BUSY;
  r->mediaConfig=*c;r->mediaConfigured=true;return RUN_OK;
 }catch(...){return RUN_INTERNAL;}
}
