#pragma once
#include <cstdint>
#include <cstddef>
#include <gst/gst.h>
#ifndef SBREC_CREDENTIALS_ABI_ONLY
#include "Auth.h"
#include <array>
#include <atomic>
#include <functional>
#include <memory>
#include <mutex>
#endif

// In-process API only: caller owns valid buffers for the duration of update and
// a live GstElement reference while binding. Handles are generation IDs, not raw
// provider pointers. No API accepts filenames, environment keys or log strings.
enum SbrecCredentialStatus : uint32_t {
    SBREC_OK = 0, SBREC_INVALID = 1, SBREC_UNAVAILABLE = 2,
    SBREC_LIMIT = 3, SBREC_INTERNAL = 4, SBREC_ALREADY_BOUND = 5
};
struct SbrecCredentialUpdate {
    const char* accessKey; uint32_t accessKeyBytes;
    const char* secretKey; uint32_t secretKeyBytes;
    const char* sessionToken; uint32_t sessionTokenBytes;
    uint64_t expiresUnixSeconds;
};
extern "C" {
__declspec(dllexport) uint32_t __cdecl sbrec_credentials_create(uint64_t* handle) noexcept;
__declspec(dllexport) uint32_t __cdecl sbrec_credentials_update(uint64_t handle, const SbrecCredentialUpdate* update) noexcept;
__declspec(dllexport) uint32_t __cdecl sbrec_credentials_release(uint64_t handle) noexcept;
__declspec(dllexport) uint32_t __cdecl sbrec_credentials_bind(GstElement* sink, uint64_t handle) noexcept;
}

#ifndef SBREC_CREDENTIALS_ABI_ONLY
namespace sbrec {
constexpr size_t MaxProviders = 8;
constexpr size_t MaxSnapshots = 32;
constexpr uint64_t ExpiryMarginSeconds = 60;
constexpr uint64_t MaxLifetimeSeconds = 960;

// The SDK auth callback returns a borrowed PAwsCredentials, including after the
// callback returns. Readers may still be using it when a replacement arrives.
// Keep immutable snapshots until the SDK adapter AND sink binding release Store;
// the hard cap prevents indefinite secret retention or growing memory use.
class CredentialStore {
public:
    using Clock = std::function<uint64_t()>; // native test seam; never a managed callback
    explicit CredentialStore(Clock clock = {});
    ~CredentialStore();
    uint32_t update(const SbrecCredentialUpdate& input) noexcept;
    STATUS read(PAwsCredentials* output) noexcept;
    size_t snapshots() const;
    static size_t live();
private:
    Clock clock_;
    mutable std::mutex mutex_;
    std::array<PAwsCredentials, MaxSnapshots> snapshots_{};
    size_t count_ = 0;
};

// These two functions are also the actual patched kvssink adapter seam exercised
// by offline tests. Selection is permanent for a binding, even with no usable
// credentials; an explicit missing/expired provider never falls back to AWS env/file.
std::unique_ptr<com::amazonaws::kinesis::video::CredentialProvider>
injected_credentials(GObject* sink, bool& selected);
std::unique_ptr<com::amazonaws::kinesis::video::CredentialProvider>
credential_adapter(std::shared_ptr<CredentialStore> store);
}
#endif
