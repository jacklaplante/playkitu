#include "native-session-credentials.h"
#include <windows.h>
#include <chrono>
#include <cstring>
#include <limits>
#include <stdexcept>

using namespace com::amazonaws::kinesis::video;
namespace {
std::atomic<size_t> liveStores{0};
struct Slot { uint64_t id = 0; std::shared_ptr<sbrec::CredentialStore> store; };
std::array<Slot, sbrec::MaxProviders> slots;
std::mutex slotsMutex;
uint64_t nextId = 1;
struct Binding { std::shared_ptr<sbrec::CredentialStore> store; };
GQuark bindingKey() { return g_quark_from_static_string("sbrec-memory-credentials-v1"); }

uint64_t utcSeconds() {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::seconds>(
        std::chrono::system_clock::now().time_since_epoch()).count());
}
std::shared_ptr<sbrec::CredentialStore> lookup(uint64_t id) {
    std::lock_guard<std::mutex> lock(slotsMutex);
    for (auto& slot : slots) if (id && slot.id == id) return slot.store;
    return {};
}
bool validText(const char* value, uint32_t size, uint32_t minimum, uint32_t maximum, bool alphanumeric) {
    if (!value || size < minimum || size > maximum) return false;
    for (uint32_t i = 0; i < size; ++i) {
        const auto c = static_cast<unsigned char>(value[i]);
        if (alphanumeric ? !((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) : (c < 33 || c > 126)) return false;
    }
    return true;
}

struct AwsMemoryProvider : AwsCredentialProvider {
    sbrec::CredentialStore* store;
    explicit AwsMemoryProvider(sbrec::CredentialStore* owner) : store(owner) {
        getCredentialsFn = [](PAwsCredentialProvider self, PAwsCredentials* output) -> STATUS {
            // No C++ exception or refresh/network work may escape into SDK C code.
            if (!self || !output) return STATUS_NULL_ARG;
            return static_cast<AwsMemoryProvider*>(self)->store->read(output);
        };
    }
};
class MemoryAdapter final : public CredentialProvider {
    std::shared_ptr<sbrec::CredentialStore> store_;
    AwsMemoryProvider provider_;
public:
    explicit MemoryAdapter(std::shared_ptr<sbrec::CredentialStore> store) : store_(std::move(store)), provider_(store_.get()) {}
    callback_t getCallbacks(PClientCallbacks client) override {
        PAuthCallbacks callbacks = nullptr;
        // AWS owns/frees its auth wrapper with the callback chain. It does NOT
        // own provider_. DefaultCallbackProvider frees that chain in its destructor
        // body before destroying this adapter; the client/streams are stopped first.
        const STATUS status = createCredentialProviderAuthCallbacks(client, &provider_, &callbacks);
        if (STATUS_FAILED(status)) throw std::runtime_error("Memory credential callback setup failed");
        return *callbacks;
    }
private:
    void updateCredentials(Credentials&) override {} // unused: SDK C provider owns callbacks
};
}

namespace sbrec {
CredentialStore::CredentialStore(Clock clock) : clock_(clock ? std::move(clock) : Clock(utcSeconds)) {
    size_t count = liveStores.load();
    do { if (count >= MaxProviders) throw std::runtime_error("Memory credential provider limit"); }
    while (!liveStores.compare_exchange_weak(count, count + 1));
}
CredentialStore::~CredentialStore() {
    // Last shared owner may disappear only after SDK callbacks are quiescent.
    // Erase every SDK allocation before free (the SDK free function only MEMFREEs).
    for (size_t i = 0; i < count_; ++i) {
        SecureZeroMemory(snapshots_[i], snapshots_[i]->size);
        freeAwsCredentials(&snapshots_[i]);
    }
    --liveStores;
}
size_t CredentialStore::live() { return liveStores.load(); }
size_t CredentialStore::snapshots() const { std::lock_guard<std::mutex> lock(mutex_); return count_; }

uint32_t CredentialStore::update(const SbrecCredentialUpdate& input) noexcept {
    try {
        if (!validText(input.accessKey, input.accessKeyBytes, 16, MAX_ACCESS_KEY_LEN, true)
            || !validText(input.secretKey, input.secretKeyBytes, 16, MAX_SECRET_KEY_LEN, false)
            || !validText(input.sessionToken, input.sessionTokenBytes, 1, MAX_SESSION_TOKEN_LEN, false)) return SBREC_INVALID;
        std::lock_guard<std::mutex> lock(mutex_);
        const uint64_t now = clock_();
        if (input.expiresUnixSeconds <= now || input.expiresUnixSeconds - now < ExpiryMarginSeconds
            || input.expiresUnixSeconds - now > MaxLifetimeSeconds
            || input.expiresUnixSeconds > UINT64_MAX / HUNDREDS_OF_NANOS_IN_A_SECOND) return SBREC_INVALID;
        if (count_) {
            auto* current = snapshots_[count_ - 1];
            const auto expiry = input.expiresUnixSeconds * HUNDREDS_OF_NANOS_IN_A_SECOND;
            if (expiry == current->expiration && current->accessKeyIdLen == input.accessKeyBytes
                && current->secretKeyLen == input.secretKeyBytes && current->sessionTokenLen == input.sessionTokenBytes
                && !memcmp(current->accessKeyId, input.accessKey, input.accessKeyBytes)
                && !memcmp(current->secretKey, input.secretKey, input.secretKeyBytes)
                && !memcmp(current->sessionToken, input.sessionToken, input.sessionTokenBytes)) return SBREC_OK;
            // Do not replace a newer grant with a delayed/replayed older response.
            if (expiry <= current->expiration) return SBREC_INVALID;
        }
        if (count_ == MaxSnapshots) return SBREC_LIMIT;
        PAwsCredentials next = nullptr;
        const STATUS status = createAwsCredentials(const_cast<PCHAR>(input.accessKey), input.accessKeyBytes,
            const_cast<PCHAR>(input.secretKey), input.secretKeyBytes, const_cast<PCHAR>(input.sessionToken), input.sessionTokenBytes,
            input.expiresUnixSeconds * HUNDREDS_OF_NANOS_IN_A_SECOND, &next);
        if (STATUS_FAILED(status)) return SBREC_INTERNAL;
        snapshots_[count_++] = next;
        return SBREC_OK;
    } catch (...) { return SBREC_INTERNAL; }
}
STATUS CredentialStore::read(PAwsCredentials* output) noexcept {
    if (!output) return STATUS_NULL_ARG;
    *output = nullptr;
    try {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!count_) return STATUS_INVALID_OPERATION;
        auto* current = snapshots_[count_ - 1];
        const auto now = clock_();
        const auto expiry = current->expiration / HUNDREDS_OF_NANOS_IN_A_SECOND;
        if (expiry <= now || expiry - now < ExpiryMarginSeconds) return STATUS_INVALID_TOKEN_EXPIRATION;
        *output = current;
        return STATUS_SUCCESS;
    } catch (...) { return STATUS_INVALID_OPERATION; }
}
std::unique_ptr<CredentialProvider> credential_adapter(std::shared_ptr<CredentialStore> store) {
    if (!store) throw std::runtime_error("Memory credentials unavailable");
    return std::unique_ptr<CredentialProvider>(new MemoryAdapter(std::move(store)));
}
std::unique_ptr<CredentialProvider> injected_credentials(GObject* sink, bool& selected) {
    auto* binding = static_cast<Binding*>(g_object_get_qdata(sink, bindingKey()));
    selected = binding != nullptr;
    if (!selected) return {};
    PAwsCredentials current = nullptr;
    if (!binding->store || STATUS_FAILED(binding->store->read(&current))) throw std::runtime_error("Memory credentials unavailable");
    return credential_adapter(binding->store);
}
}

extern "C" uint32_t __cdecl sbrec_credentials_create(uint64_t* handle) noexcept {
    if (!handle) return SBREC_INVALID;
    *handle = 0;
    try {
        std::lock_guard<std::mutex> lock(slotsMutex);
        if (sbrec::CredentialStore::live() >= sbrec::MaxProviders || nextId == UINT64_MAX) return SBREC_LIMIT;
        for (auto& slot : slots) if (!slot.store) {
            slot.store = std::make_shared<sbrec::CredentialStore>();
            slot.id = nextId++;
            *handle = slot.id;
            return SBREC_OK;
        }
        return SBREC_LIMIT;
    } catch (...) { return SBREC_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_credentials_update(uint64_t handle, const SbrecCredentialUpdate* input) noexcept {
    if (!input) return SBREC_INVALID;
    try { auto store = lookup(handle); return store ? store->update(*input) : SBREC_INVALID; }
    catch (...) { return SBREC_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_credentials_release(uint64_t handle) noexcept {
    try {
        std::lock_guard<std::mutex> lock(slotsMutex);
        for (auto& slot : slots) if (handle && slot.id == handle) {
            slot.store.reset(); slot.id = 0; return SBREC_OK;
        }
        return SBREC_INVALID;
    } catch (...) { return SBREC_INTERNAL; }
}
extern "C" uint32_t __cdecl sbrec_credentials_bind(GstElement* sink, uint64_t handle) noexcept {
    if (!sink || strcmp(G_OBJECT_TYPE_NAME(sink), "GstKvsSink")) return SBREC_INVALID;
    try {
        auto store = lookup(handle);
        GST_STATE_LOCK(sink);
        if (GST_STATE(sink) != GST_STATE_NULL || GST_STATE_PENDING(sink) != GST_STATE_VOID_PENDING) {
            GST_STATE_UNLOCK(sink); return SBREC_INVALID;
        }
        if (g_object_get_qdata(G_OBJECT(sink), bindingKey())) {
            GST_STATE_UNLOCK(sink); return SBREC_ALREADY_BOUND;
        }
        // Invalid handle still installs an explicit, permanently unavailable
        // binding. Ignoring an API error cannot accidentally enable ambient auth.
        static Binding unavailable;
        g_object_set_qdata(G_OBJECT(sink), bindingKey(), &unavailable);
        if (!store) { GST_STATE_UNLOCK(sink); return SBREC_INVALID; }
        Binding* binding = nullptr;
        try { binding = new Binding{store}; }
        catch (...) { GST_STATE_UNLOCK(sink); return SBREC_INTERNAL; }
        g_object_set_qdata_full(G_OBJECT(sink), bindingKey(), binding, [](gpointer data) { delete static_cast<Binding*>(data); });
        GST_STATE_UNLOCK(sink);
        return SBREC_OK;
    } catch (...) { return SBREC_INTERNAL; }
}
