// B0a local synthetic proof only. No device sources, credentials, network sink,
// production Unity capture or software H.264 fallback can be selected here.
#include <gst/gst.h>
#include <windows.h>
#include <filesystem>
#include <iostream>
#include <string>
#include <vector>
#include <limits>

#include "native-synthetic-media.h"

static void summary(const Samples& s) {
    std::cout << s.name << "_caps=" << s.caps << '\n'
              << s.name << "_buffers=" << s.count << '\n'
              << s.name << "_bytes=" << s.bytes << '\n'
              << s.name << "_first_pts_ns=" << s.first << '\n'
              << s.name << "_last_pts_ns=" << s.last << '\n'
              << s.name << "_segment_start_ns=" << s.segment.start << '\n'
              << s.name << "_segment_base_ns=" << s.segment.base << '\n'
              << s.name << "_first_running_ns=" << s.firstRunning << '\n'
              << s.name << "_last_running_ns=" << s.lastRunning << '\n'
              << s.name << "_running_monotonic=" << s.runningMonotonic << '\n'
              << s.name << "_monotonic=" << s.monotonic << '\n'
              << s.name << "_keys=" << s.keys << '\n'
              << s.name << "_max_key_gap_ns=" << s.maxKeyGap << '\n';
}

static std::string utf8(const wchar_t* value) {
    const int n = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    std::string result(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), n, nullptr, nullptr);
    result.pop_back();
    return result;
}

static int number(const wchar_t* value, int low, int high) {
    wchar_t* end = nullptr;
    const long n = wcstol(value, &end, 10);
    return *value && !*end && n >= low && n <= high ? static_cast<int>(n) : -1;
}

int run_native_proof(int argc, wchar_t** argv) {
    const bool encode = argc == 7 && !wcscmp(argv[1], L"encode");
    const bool decode = argc == 3 && !wcscmp(argv[1], L"decode");
    const bool probe = argc == 2 && !wcscmp(argv[1], L"probe");
    const int seconds = encode ? number(argv[3], 1, 60) : 0;
    const int width = encode ? number(argv[4], 64, 1280) : 0;
    const int height = encode ? number(argv[5], 64, 720) : 0;
    const int fps = encode ? number(argv[6], 1, 30) : 0;
    if ((!encode && !decode && !probe) || (encode && (seconds < 0 || width < 0 || height < 0 || fps < 0 || width % 2 || height % 2))) {
        std::cerr << "Usage: native-proof probe | encode ABS_OUTPUT seconds(1..60) even_width(64..1280) even_height(64..720) fps(1..30) | decode ABS_INPUT\n";
        return 2;
    }
    if (!probe && !std::filesystem::path(argv[2]).is_absolute()) return 2;
    if (decode && !std::filesystem::is_regular_file(argv[2])) return 2;
    // Never overwrite a previous successful artifact, even if a caller bypasses
    // the PowerShell runner. The file is ours only after CREATE_NEW succeeds.
    if (encode) {
        HANDLE file = CreateFileW(argv[2], GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) { std::cerr << "Cannot reserve new output\n"; return 2; }
        CloseHandle(file);
    }
    gst_init(nullptr, nullptr);
    gchar* version = gst_version_string();
    std::cout << "native_pid=" << GetCurrentProcessId() << '\n'
              << "gstreamer_version=" << version << '\n';
    g_free(version);
    if (probe) {
        // Reading the factory and pad templates never transitions kvssink into
        // READY/PAUSED/PLAYING, which could initialize AWS or discover credentials.
        GstElementFactory* factory = gst_element_factory_find("kvssink");
        if (!factory) return 3;
        // Registry metadata alone does not prove DLL loading in this process.
        // Load the plugin feature, without constructing or changing sink state.
        GstPluginFeature* loaded = gst_plugin_feature_load(GST_PLUGIN_FEATURE(factory));
        gst_object_unref(factory);
        if (!loaded) return 3;
        factory = GST_ELEMENT_FACTORY(loaded);
        GstPlugin* plugin = gst_plugin_feature_get_plugin(GST_PLUGIN_FEATURE(factory));
        std::cout << "kvssink_plugin=" << gst_plugin_get_filename(plugin) << '\n';
        gst_object_unref(plugin);
        for (const wchar_t* name : {L"PlaytestRecordingNativeProof.dll", L"gstreamer-1.0-0.dll", L"gstkvssink.dll", L"libcurl.dll", L"libcrypto-3-x64.dll", L"libssl-3-x64.dll", L"log4cplus.dll", L"msvcp140.dll", L"vcruntime140.dll", L"vcruntime140_1.dll"}) {
            HMODULE module = GetModuleHandleW(name);
            wchar_t path[32768];
            if (module && GetModuleFileNameW(module, path, 32768)) std::cout << "module_" << utf8(name) << '=' << utf8(path) << '\n';
        }
        for (const GList* p = gst_element_factory_get_static_pad_templates(factory); p; p = p->next) {
            auto* t = static_cast<GstStaticPadTemplate*>(p->data);
            GstCaps* caps = gst_static_caps_get(&t->static_caps);
            gchar* text = gst_caps_to_string(caps);
            std::cout << t->name_template << "=" << text << '\n';
            g_free(text); gst_caps_unref(caps);
        }
        gst_object_unref(factory);
        return 0;
    }
    std::string pipeline;
    if (encode) {
        GstElementFactory* factory = gst_element_factory_find("mfh264enc");
        const char* klass = factory ? gst_element_factory_get_metadata(factory, GST_ELEMENT_METADATA_KLASS) : nullptr;
        const bool hardware = klass && strstr(klass, "Hardware");
        if (factory) gst_object_unref(factory);
        if (!hardware) { DeleteFileW(argv[2]); std::cerr << "Hardware mfh264enc unavailable; fallback disabled\n"; return 3; }
        // Separate encoded AV pads are the kvssink contract. matroskamux here is
        // strictly offline validation; it is not the future AWS ingress format.
        pipeline = "matroskamux name=mux ! filesink name=file " + synthetic_media(seconds, width, height, fps, "mux.", false);
    } else {
        // Decode all samples to fakesinks: validates playability without opening
        // speakers, a video window, microphone, desktop or other capture devices.
        pipeline = "filesrc name=file ! matroskademux name=demux "
            "demux.video_0 ! queue ! h264parse ! d3d11h264dec ! identity name=video ! fakesink sync=false "
            "demux.audio_0 ! queue ! aacparse ! mfaacdec ! identity name=audio ! fakesink sync=false";
    }
    GError* error = nullptr;
    GstElement* root = gst_parse_launch(pipeline.c_str(), &error);
    int result = 0;
    if (error || !root) {
        std::cerr << (error ? error->message : "parse failed") << '\n';
        if (error) g_error_free(error);
        if (root) gst_object_unref(root);
        if (encode) DeleteFileW(argv[2]);
        return 3;
    }
    GstElement* file = gst_bin_get_by_name(GST_BIN(root), "file");
    const auto path = utf8(argv[2]);
    g_object_set(file, "location", path.c_str(), nullptr);
    gst_object_unref(file);
    Samples video{"video"}, audio{"audio"};
    for (auto* s : {&video, &audio}) {
        GstElement* e = gst_bin_get_by_name(GST_BIN(root), s->name);
        GstPad* pad = gst_element_get_static_pad(e, "src");
        gst_pad_add_probe(pad, GST_PAD_PROBE_TYPE_BUFFER, observe, s, nullptr);
        gst_object_unref(pad); gst_object_unref(e);
    }
    GstBus* bus = gst_element_get_bus(root);
    if (gst_element_set_state(root, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) result = 3;
    else {
        GstMessage* message = gst_bus_timed_pop_filtered(bus, (encode ? seconds + 15 : 75) * GST_SECOND,
            static_cast<GstMessageType>(GST_MESSAGE_ERROR | GST_MESSAGE_EOS));
        if (!message) result = 4;
        else {
            if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_ERROR) {
                gchar* debug = nullptr;
                gst_message_parse_error(message, &error, &debug);
                std::cerr << error->message << '\n' << (debug ? debug : "") << '\n';
                g_error_free(error); g_free(debug); result = 3;
            }
            gst_message_unref(message);
        }
    }
    gst_element_set_state(root, GST_STATE_NULL);
    gst_object_unref(bus); gst_object_unref(root);
    summary(video); summary(audio);
    if (!video.count || !audio.count || !video.monotonic || !audio.monotonic || !video.runningMonotonic || !audio.runningMonotonic) result = 3;
    if (encode && result) DeleteFileW(argv[2]);
    return result;
}
