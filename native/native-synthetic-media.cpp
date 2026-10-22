#include "native-synthetic-media.h"
#include <algorithm>

std::atomic<bool> sbrec_native_owned{false};
GstPadProbeReturn observe(GstPad* pad, GstPadProbeInfo* info, gpointer data) {
    auto& s = *static_cast<Samples*>(data);
    GstBuffer* b = GST_PAD_PROBE_INFO_BUFFER(info);
    const auto pts = GST_BUFFER_PTS(b);
    if (!s.count) {
        s.first = pts;
        GstCaps* caps = gst_pad_get_current_caps(pad);
        gchar* text = caps ? gst_caps_to_string(caps) : g_strdup("unset");
        s.caps = text; g_free(text);
        if (caps) gst_caps_unref(caps);
        GstEvent* event = gst_pad_get_sticky_event(pad, GST_EVENT_SEGMENT, 0);
        gst_segment_init(&s.segment, GST_FORMAT_TIME);
        if (event) {
            const GstSegment* segment;
            gst_event_parse_segment(event, &segment);
            gst_segment_copy_into(segment, &s.segment); gst_event_unref(event);
        }
    }
    const auto running = gst_segment_to_running_time(&s.segment, GST_FORMAT_TIME, pts);
    if (!s.count) s.firstRunning = running;
    if (!GST_CLOCK_TIME_IS_VALID(running) || (s.count && running < s.lastRunning)) s.runningMonotonic = false;
    s.lastRunning = running;
    if (!GST_CLOCK_TIME_IS_VALID(pts) || (s.count && pts < s.last)) s.monotonic = false;
    s.last = pts; s.count++; s.bytes += gst_buffer_get_size(b);
    if (!GST_BUFFER_FLAG_IS_SET(b, GST_BUFFER_FLAG_DELTA_UNIT)) {
        if (GST_CLOCK_TIME_IS_VALID(s.previousKey)) s.maxKeyGap = (std::max)(s.maxKeyGap, pts - s.previousKey);
        s.previousKey = pts; s.keys++;
    }
    return GST_PAD_PROBE_OK;
}

namespace {
std::string encoded_media(std::string p, const std::string& audioSource, int fps, const char* dest, bool counter, const SbrecMediaConfigV1* options) {
    SbrecMediaConfigV1 defaults;defaults.gopFrames=fps;const auto& config=options?*options:defaults;
    if (counter) p += "! timeoverlay time-mode=running-time font-desc=\"Consolas 28\" valignment=top halignment=left "
        "! textoverlay name=counter text=\"frame 0\" font-desc=\"Consolas 28\" valignment=bottom halignment=left ";
    p += "! videoconvert ! video/x-raw,format=NV12 ! mfh264enc name=encoder bitrate=" + std::to_string(config.videoKbps) + " max-bitrate=" + std::to_string(config.maxVideoKbps) + " rc-mode=cbr bframes=0 low-latency=true gop-size=" + std::to_string(config.gopFrames) +
        " ! h264parse name=video ! video/x-h264,stream-format=avc,alignment=au ! queue max-size-buffers=" + std::to_string(config.videoQueue) + " max-size-bytes=0 max-size-time=0 ! " + dest + " "
        + audioSource + " ! audioconvert ! audio/x-raw,format=S16LE,rate=48000,channels=2 ! mfaacenc bitrate=" + std::to_string(config.audioBps) + " ! aacparse name=audio "
        "! audio/mpeg,mpegversion=4,stream-format=raw ! queue max-size-buffers=" + std::to_string(config.audioQueue) + " max-size-bytes=0 max-size-time=0 ! " + dest;
    return p;
}
}
std::string synthetic_media(int seconds, int width, int height, int fps, const char* dest, bool counter, const SbrecMediaConfigV1* options) {
    auto video = "videotestsrc name=sourcevideo is-live=true num-buffers=" + std::to_string(seconds * fps) +
        " ! video/x-raw,format=" + (counter ? "BGRA" : "NV12") + ",width=" + std::to_string(width) + ",height=" + std::to_string(height) + ",framerate=" + std::to_string(fps) + "/1 ";
    auto audio = "audiotestsrc name=sourceaudio is-live=true samplesperbuffer=480 num-buffers=" + std::to_string(seconds * 100) +
        " ! audio/x-raw,format=S16LE,rate=48000,channels=2 ";
    return encoded_media(video,audio,fps,dest,counter,options);
}
std::string external_media(int width, int height, int fps, const char* dest, const SbrecMediaConfigV1* options) {
    SbrecMediaConfigV1 defaults;const auto& config=options?*options:defaults;
    auto video = "appsrc name=sourcevideo is-live=true format=time block=false max-buffers=" + std::to_string(config.inputVideo) + " max-bytes=0 max-time=0 leaky-type=downstream caps=\"video/x-raw,format=RGBA,width="
        + std::to_string(width) + ",height=" + std::to_string(height) + ",framerate=" + std::to_string(fps) + "/1\" ";
    std::string audio = std::string("appsrc name=sourceaudio is-live=true format=time block=false max-buffers=") + std::to_string(config.inputAudio) + " max-bytes=0 max-time=0 leaky-type=downstream caps=\"audio/x-raw,format=F32LE,layout=interleaved,rate=48000,channels=2\" ";
    return encoded_media(video,audio,fps,dest,false,options);
}
