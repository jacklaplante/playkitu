#include "native-encoder-selection.h"
#include "native-synthetic-media.h"
#include <windows.h>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <cwchar>

namespace sbrec_encoder {
namespace {
using Steady = std::chrono::steady_clock;
struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle() = default;
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
bool factory_name(const std::string& name) {
    if (name == "mfh264enc") return true;
    const std::string prefix = "mfh264device", suffix = "enc";
    if (name.size() <= prefix.size() + suffix.size() || name.compare(0, prefix.size(), prefix)
        || name.compare(name.size() - suffix.size(), suffix.size(), suffix)) return false;
    return name[prefix.size()] != '0' && std::all_of(name.begin() + prefix.size(), name.end() - suffix.size(),
        [](char c) { return c >= '0' && c <= '9'; });
}
bool hardware(const std::string& name) {
    if (!factory_name(name)) return false;
    auto* factory = gst_element_factory_find(name.c_str());
    const char* klass = factory ? gst_element_factory_get_metadata(factory, GST_ELEMENT_METADATA_KLASS) : nullptr;
    const bool result = klass && std::strstr(klass, "Hardware");
    if (factory) gst_object_unref(factory);
    return result;
}
std::filesystem::path helper_path() {
    HMODULE module = nullptr;
    // Resolve beside this exact DLL/EXE, never through PATH or the caller's cwd.
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&select_hardware), &module)) return {};
    wchar_t path[32768];
    const auto length = GetModuleFileNameW(module, path, 32768);
    if (!length || length >= 32768) return {};
    return std::filesystem::path(path).parent_path() / L"native-proof.exe";
}
}

ProbeResult run_probe(const std::filesystem::path& executable, const std::wstring& arguments,
    uint32_t timeoutMs, Cancelled cancelled, void* context) {
    if (!timeoutMs || !executable.is_absolute() || !std::filesystem::is_regular_file(executable)) return ProbeResult::Unavailable;
    if (cancelled && cancelled(context)) return ProbeResult::Cancelled;
    const auto deadline = Steady::now() + std::chrono::milliseconds(timeoutMs);
    Handle job;
    job.value = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job.value || !SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return ProbeResult::Unavailable;

    // Do not pass AWS credentials/configuration or arbitrary host environment to
    // the generated encoder check. Only the existing scoped GStreamer runtime,
    // cache and Windows loader inputs cross this process boundary.
    std::vector<std::wstring> entries;
    for (const wchar_t* name : {L"SystemRoot", L"WINDIR", L"TEMP", L"TMP", L"LOCALAPPDATA",
        L"GST_PLUGIN_PATH", L"GST_PLUGIN_PATH_1_0", L"GST_PLUGIN_SYSTEM_PATH", L"GST_PLUGIN_SYSTEM_PATH_1_0",
        L"GST_PLUGIN_SCANNER", L"GST_PLUGIN_SCANNER_1_0", L"GST_REGISTRY", L"GST_REGISTRY_1_0"}) {
        const DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
        if (!size || size > 32768) continue;
        std::vector<wchar_t> value(size);
        const DWORD actual = GetEnvironmentVariableW(name, value.data(), size);
        if (actual && actual < size) entries.emplace_back(std::wstring(name) + L"=" + value.data());
    }
    wchar_t system[32768];
    const UINT systemLength = GetSystemDirectoryW(system, 32768);
    if (!systemLength || systemLength >= 32768) return ProbeResult::Unavailable;
    entries.emplace_back(L"PATH=" + executable.parent_path().wstring() + L";" + system);
    entries.emplace_back(L"GST_DEBUG=0");
    std::sort(entries.begin(), entries.end(), [](const std::wstring& a, const std::wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) < 0; });
    std::vector<wchar_t> environment;
    for (const auto& entry : entries) { environment.insert(environment.end(), entry.begin(), entry.end()); environment.push_back(0); }
    environment.push_back(0);
    std::wstring command = L"\"" + executable.wstring() + L"\" " + arguments;
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    // Start suspended so even a plugin scanner belongs to the generated-only
    // job before any child code runs. Cancellation never kills a live recorder.
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE,
        CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
        environment.data(), executable.parent_path().c_str(), &startup, &process)) return ProbeResult::Unavailable;
    Handle child, thread; child.value = process.hProcess; thread.value = process.hThread;
    if (!AssignProcessToJobObject(job.value, child.value)) { TerminateProcess(child.value, 3); WaitForSingleObject(child.value, 1000); return ProbeResult::Unavailable; }
    if (ResumeThread(thread.value) == DWORD(-1)) return ProbeResult::Unavailable;
    while (true) {
        const DWORD state = WaitForSingleObject(child.value, 25);
        if (state == WAIT_OBJECT_0) {
            DWORD code = 3;
            return GetExitCodeProcess(child.value, &code) && code == 0 ? ProbeResult::Passed : ProbeResult::Rejected;
        }
        if (state == WAIT_FAILED) return ProbeResult::Unavailable;
        const bool stop = cancelled && cancelled(context);
        if (stop || Steady::now() >= deadline) {
            TerminateJobObject(job.value, 124);
            WaitForSingleObject(child.value, 1000);
            return stop ? ProbeResult::Cancelled : ProbeResult::TimedOut;
        }
    }
}

std::string choose_candidates(const std::vector<std::string>& candidates, const Probe& probe,
    uint32_t totalMs, uint32_t candidateMs, Cancelled cancelled, void* context) {
    const auto deadline = Steady::now() + std::chrono::milliseconds(totalMs);
    // Bound startup work independently of driver/adapter count. A failed check
    // never grants a new deadline or permits a software encoder fallback.
    size_t attempted = 0;
    for (const auto& candidate : candidates) {
        if (!factory_name(candidate)) continue;
        if ((cancelled && cancelled(context)) || attempted++ >= 4) break;
        auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - Steady::now()).count();
        if (remaining <= 0) break;
        const auto result = probe(candidate, static_cast<uint32_t>((std::min)(remaining, static_cast<long long>(candidateMs))));
        if (result == ProbeResult::Cancelled || (cancelled && cancelled(context))) break;
        if (result == ProbeResult::Passed && Steady::now() < deadline) return candidate;
    }
    return {};
}

std::string select_hardware(uint32_t width, uint32_t height, uint32_t fps, const SbrecMediaConfigV1& config,
    Cancelled cancelled, void* context) {
    std::vector<std::string> candidates;
    GList* features = gst_registry_get_feature_list(gst_registry_get(), GST_TYPE_ELEMENT_FACTORY);
    for (auto* node = features; node; node = node->next) {
        const char* name = gst_plugin_feature_get_name(GST_PLUGIN_FEATURE(node->data));
        if (name && hardware(name)) candidates.emplace_back(name);
    }
    gst_plugin_feature_list_free(features);
    std::sort(candidates.begin(), candidates.end(), [](const std::string& a, const std::string& b) {
        if (a == "mfh264enc" || b == "mfh264enc") return a == "mfh264enc" && b != "mfh264enc";
        if (a.size() != b.size()) return a.size() < b.size();
        return a < b;
    });
    candidates.erase(std::unique(candidates.begin(), candidates.end()), candidates.end());
    const auto executable = helper_path();
    return choose_candidates(candidates, [&](const std::string& factory, uint32_t timeout) {
        const std::wstring arguments = L"check-encoder " + std::wstring(factory.begin(), factory.end())
            + L" " + std::to_wstring(width) + L" " + std::to_wstring(height) + L" " + std::to_wstring(fps)
            + L" " + std::to_wstring(config.videoKbps) + L" " + std::to_wstring(config.maxVideoKbps)
            + L" " + std::to_wstring(config.gopFrames);
        return run_probe(executable, arguments, timeout, cancelled, context);
    }, 10000, 5000, cancelled, context);
}

int check_hardware(const std::string& factory, uint32_t width, uint32_t height, uint32_t fps, const SbrecMediaConfigV1& config) {
    gst_init(nullptr, nullptr);
    if (!hardware(factory)) return 3;
    // Same encoding configuration as capture, with generated video/tone and
    // discard sinks only. Prove actual output and clean teardown, not metadata.
    // The public profile permits 1 FPS. Keep the configured rate, but generate
    // two seconds there so the two-output-frame proof can actually succeed.
    const int seconds = fps == 1 ? 2 : 1;
    const auto graph = synthetic_media(seconds, width, height, fps, "fakesink sync=false async=false", false, &config, factory.c_str());
    GError* error = nullptr;
    auto* pipeline = gst_parse_launch(graph.c_str(), &error);
    if (error || !pipeline) { if (error) g_error_free(error); if (pipeline) gst_object_unref(pipeline); return 3; }
    Samples video{"video"}, audio{"audio"};
    for (auto* sample : {&video, &audio}) {
        auto* element = gst_bin_get_by_name(GST_BIN(pipeline), sample->name);
        auto* pad = gst_element_get_static_pad(element, "src");
        gst_pad_add_probe(pad, GST_PAD_PROBE_TYPE_BUFFER, observe, sample, nullptr);
        gst_object_unref(pad); gst_object_unref(element);
    }
    auto* bus = gst_element_get_bus(pipeline);
    bool passed = false;
    if (gst_element_set_state(pipeline, GST_STATE_PLAYING) != GST_STATE_CHANGE_FAILURE) {
        auto* message = gst_bus_timed_pop_filtered(bus, (seconds + 1) * GST_SECOND, GstMessageType(GST_MESSAGE_ERROR | GST_MESSAGE_EOS));
        passed = message && GST_MESSAGE_TYPE(message) == GST_MESSAGE_EOS;
        if (message) gst_message_unref(message);
    }
    // A driver may hang here too. The parent waits for process exit and reaps
    // the whole isolated job on its fixed deadline, even after encoded output.
    gst_element_set_state(pipeline, GST_STATE_NULL);
    gst_object_unref(bus); gst_object_unref(pipeline);
    return passed && video.count >= 2 && audio.count >= 2 && video.monotonic && audio.monotonic ? 0 : 3;
}
}
