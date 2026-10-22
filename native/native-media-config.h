#pragma once
#include <cstdint>
// Additive CREATED-only settings. Existing SbrecRunSettings/start ABI1 stays intact.
// All fields have explicit units, no strings, pointers, arbitrary encoders or pipelines.
struct SbrecMediaConfigV1 {
    uint32_t size=64,version=1,encoder=1,videoKbps=1800,maxVideoKbps=2000,audioBps=96000,gopFrames=30;
    uint32_t videoQueue=30,audioQueue=100,inputVideo=2,inputAudio=8;
    uint32_t mediaBps=294912,creditBytes=4096,readBytes=4096,waitMs=100,sliceMs=2;
};
static_assert(sizeof(SbrecMediaConfigV1)==64,"media config V1 x64 layout");
inline bool sbrec_valid_media(const SbrecMediaConfigV1& c) {
 return c.size==sizeof(c)&&c.version==1&&c.encoder==1&&c.videoKbps>=200&&c.videoKbps<=2000&&
 c.maxVideoKbps>=c.videoKbps&&c.maxVideoKbps<=2000&&c.audioBps==96000&&
 c.gopFrames>=1&&c.gopFrames<=60&&c.videoQueue>=1&&c.videoQueue<=30&&c.audioQueue>=1&&c.audioQueue<=100&&
 c.inputVideo>=1&&c.inputVideo<=2&&c.inputAudio>=1&&c.inputAudio<=8&&c.mediaBps>=65536&&c.mediaBps<=294912&&
 c.mediaBps>=(c.maxVideoKbps*1000+c.audioBps)/8+8192&&c.creditBytes>=512&&c.creditBytes<=4096&&
 c.readBytes>=512&&c.readBytes<=4096&&c.readBytes<=c.creditBytes&&c.waitMs>=20&&c.waitMs<=100&&c.sliceMs>=1&&c.sliceMs<=5&&
 (uint64_t)c.readBytes*1000<=(uint64_t)c.mediaBps*(c.waitMs-c.sliceMs);
}

