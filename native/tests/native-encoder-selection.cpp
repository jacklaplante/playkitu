#include "../native-encoder-selection.h"
#include <windows.h>
#include <chrono>
#include <cstdio>
#include <cwchar>

static unsigned checks = 0;
#define CHECK(value) do { ++checks; if (!(value)) { std::printf("FAIL line=%d expression=%s\n", __LINE__, #value); return 1; } } while (0)
using Clock = std::chrono::steady_clock;
static bool cancelled(void* context) { return Clock::now() >= *static_cast<Clock::time_point*>(context); }

int wmain(int argc, wchar_t** argv) {
    if (argc == 3 && !wcscmp(argv[1], L"--probe-child")) {
        if (!wcscmp(argv[2], L"hang")) Sleep(INFINITE);
        if (!wcscmp(argv[2], L"reject")) return 7;
        // The live game's media grant must never be inherited by this killable
        // generated-only child. The fake parent's value is not printed anywhere.
        return GetEnvironmentVariableW(L"AWS_SECRET_ACCESS_KEY", nullptr, 0) == 0 ? 0 : 8;
    }
    using namespace sbrec_encoder;
    wchar_t path[32768]; CHECK(GetModuleFileNameW(nullptr, path, 32768));
    const std::filesystem::path exe(path);
    CHECK(run_probe(exe.parent_path() / "missing.exe", L"", 100) == ProbeResult::Unavailable);
    SetEnvironmentVariableW(L"AWS_SECRET_ACCESS_KEY", L"OFFLINE_ONLY_PROBE_ENVIRONMENT_SENTINEL");
    CHECK(run_probe(exe, L"--probe-child pass", 2000) == ProbeResult::Passed);
    SetEnvironmentVariableW(L"AWS_SECRET_ACCESS_KEY", nullptr);
    CHECK(run_probe(exe, L"--probe-child reject", 2000) == ProbeResult::Rejected);
    auto began = Clock::now();
    CHECK(run_probe(exe, L"--probe-child hang", 100) == ProbeResult::TimedOut);
    CHECK(Clock::now() - began < std::chrono::seconds(2));
    auto stopAt = Clock::now() + std::chrono::milliseconds(50);
    CHECK(run_probe(exe, L"--probe-child hang", 2000, cancelled, &stopAt) == ProbeResult::Cancelled);

    unsigned attempted = 0;
    auto selected = choose_candidates({"x264enc", "mfh264enc", "mfh264device1enc"}, [&](const std::string& name, uint32_t budget) {
        ++attempted;
        return run_probe(exe, name == "mfh264enc" ? L"--probe-child hang" : L"--probe-child pass", budget);
    }, 2000, 100);
    CHECK(selected == "mfh264device1enc" && attempted == 2);
    attempted = 0;
    selected = choose_candidates({"mfh264enc", "mfh264device1enc"}, [&](const std::string&, uint32_t budget) {
        ++attempted; return run_probe(exe, L"--probe-child hang", budget);
    }, 100, 2000);
    CHECK(selected.empty() && attempted == 1);
    attempted = 0;
    selected = choose_candidates({"mfh264enc", "mfh264device1enc"}, [&](const std::string&, uint32_t) {
        ++attempted; return ProbeResult::Cancelled;
    });
    CHECK(selected.empty() && attempted == 1);
    selected = choose_candidates({"mfh264enc"}, [&](const std::string&, uint32_t) {
        Sleep(40); return ProbeResult::Passed;
    }, 20, 20);
    CHECK(selected.empty()); // A late success cannot renew the startup budget.
    attempted = 0;
    selected = choose_candidates({"mfh264enc", "mfh264device1enc", "mfh264device2enc", "mfh264device3enc", "mfh264device4enc"},
        [&](const std::string&, uint32_t) { ++attempted; return ProbeResult::Rejected; });
    CHECK(selected.empty() && attempted == 4);
    std::printf("PASS encoder_selection_checks=%u\n", checks);
    return 0;
}
