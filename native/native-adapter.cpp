// Local synthetic IL2CPP ABI proof, not the production recording bridge.
#include <windows.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <mutex>
#include <string>
#include <vector>
#include "native-synthetic-media.h"

int run_native_proof(int argc, wchar_t** argv);
static std::mutex invocation;
extern "C" __declspec(dllexport) int __cdecl proof_abi_version() { return 1; }

// No arbitrary pipeline, callbacks, credentials, capture devices or network mode.
// Serialization also protects stream redirection and the process-wide GST registry.
extern "C" __declspec(dllexport) int __cdecl proof_run(const wchar_t* mode,
    const wchar_t* media, int seconds, int width, int height, int fps, const wchar_t* report) {
    std::lock_guard<std::mutex> guard(invocation);
    if (sbrec_native_owned.exchange(true)) return 6;
    struct Owner { ~Owner() { sbrec_native_owned = false; } } owner;
    if (!mode || !media || !report || !std::filesystem::path(report).is_absolute()) return 2;
    HANDLE reserved = CreateFileW(report, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (reserved == INVALID_HANDLE_VALUE) return 5;
    CloseHandle(reserved);
    std::ofstream log(std::filesystem::path(report), std::ios::app);
    if (!log) return 5;
    auto* out = std::cout.rdbuf(log.rdbuf());
    auto* err = std::cerr.rdbuf(log.rdbuf());
    int result = 3;
    try {
        std::vector<std::wstring> arguments{L"native-proof", mode};
        if (wcscmp(mode, L"probe")) arguments.push_back(media);
        if (!wcscmp(mode, L"encode")) {
            for (int value : {seconds, width, height, fps}) arguments.push_back(std::to_wstring(value));
        }
        std::vector<wchar_t*> argv;
        for (auto& argument : arguments) argv.push_back(argument.data());
        result = run_native_proof(static_cast<int>(argv.size()), argv.data());
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; }
    catch (...) { std::cerr << "Unexpected native proof failure\n"; }
    log.flush();
    if (!log) result = 5;
    std::cout.rdbuf(out);
    std::cerr.rdbuf(err);
    return result;
}
