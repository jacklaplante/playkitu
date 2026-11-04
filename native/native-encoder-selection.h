#pragma once
#include "native-media-config.h"
#include <cstdint>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

namespace sbrec_encoder {
enum class ProbeResult { Passed, Rejected, TimedOut, Cancelled, Unavailable };
using Cancelled = bool (*)(void*);
using Probe = std::function<ProbeResult(const std::string&, uint32_t)>;

// Generated-only checks run outside the game because an installed hardware MFT
// can deadlock in both ProcessInput and shutdown. Never use this killable owner
// for capture, credentials, kvssink or the live recording worker.
ProbeResult run_probe(const std::filesystem::path& executable, const std::wstring& arguments,
    uint32_t timeoutMs, Cancelled cancelled = nullptr, void* context = nullptr);
std::string choose_candidates(const std::vector<std::string>& candidates, const Probe& probe,
    uint32_t totalMs = 10000, uint32_t candidateMs = 5000,
    Cancelled cancelled = nullptr, void* context = nullptr);
std::string select_hardware(uint32_t width, uint32_t height, uint32_t fps, const SbrecMediaConfigV1& config,
    Cancelled cancelled = nullptr, void* context = nullptr);
int check_hardware(const std::string& factory, uint32_t width, uint32_t height, uint32_t fps,
    const SbrecMediaConfigV1& config);
}
