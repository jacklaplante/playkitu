// Offline tests use explicit fake credentials, native fake clocks, and SDK C auth
// callbacks only. Never create a producer/client stream or call AWS transports.
#include "native-session-credentials.h"
#include "native-synthetic-media.h"
#include <windows.h>
#include <thread>
#include <vector>
#include <iostream>
#include <stdexcept>
#include <cstring>
#include <chrono>

using namespace com::amazonaws::kinesis::video;
int run_sdk_logging_tests();
static int checks = 0;
static void require(bool ok, const char* label) {
    if (!ok) throw std::runtime_error(label); // labels are fixed, never secret input
    ++checks;
}
static SbrecCredentialUpdate fake(uint64_t expiry) {
    return {"FAKEACCESS00000000001", 20, "FAKE_SECRET_NOT_A_REAL_AWS_KEY_0000000000", 39,
        "FAKE_SESSION_TOKEN_NOT_VALID_FOR_AWS", 36, expiry};
}
struct Chain {
    PClientCallbacks client = nullptr;
    std::unique_ptr<CredentialProvider> adapter;
    AuthCallbacks auth{};
    explicit Chain(std::shared_ptr<sbrec::CredentialStore> store) : adapter(sbrec::credential_adapter(std::move(store))) {
        require(STATUS_SUCCEEDED(createAbstractDefaultCallbacksProvider(DEFAULT_CALLBACK_CHAIN_COUNT,
            API_CALL_CACHE_TYPE_NONE, 0, const_cast<PCHAR>("us-east-2"), const_cast<PCHAR>("https://127.0.0.1:1"),
            nullptr, nullptr, nullptr, &client)), "offline SDK callback chain");
        auth = adapter->getCallbacks(client);
    }
    ~Chain() { freeCallbacksProvider(&client); } // SDK wrapper first, then adapter/store
    STATUS read(PAwsCredentials* value) {
        PBYTE data = nullptr; UINT32 size = 0; UINT64 expiry = 0;
        auto status = auth.getSecurityTokenFn(auth.customData, &data, &size, &expiry);
        *value = reinterpret_cast<PAwsCredentials>(data);
        if (STATUS_SUCCEEDED(status)) require(size == (*value)->size && expiry == (*value)->expiration, "SDK preserves actual expiration");
        return status;
    }
};

int main(int argc, char** argv) {
    try {
        require(argc == 2, "plugin path required");
        // E51: UTC rebasing must share the SDK's existing PTS/DTS timecode grid.
        // Exercise every sub-ms phase, exact source spacing, and arithmetic bounds.
        for(uint64_t phase=0;phase<1000000;phase+=100) {
            uint64_t a=0,b=0;const uint64_t first=24106400,utc=1792595958681000000ULL+phase;
            require(sbrec_sdk_utc_pts_ns(utc,first,first,1000000,a),"UTC mapping valid");
            require(sbrec_sdk_utc_pts_ns(utc,first,first+33333333,1000000,b),"later UTC mapping valid");
            require((a-first)%1000000==0 && utc-a<1000000,"integral offset and sub-ms UTC error");
            require(b-a==33333333,"source interval preserved exactly");
        }
        uint64_t unchanged=123;
        require(!sbrec_sdk_utc_pts_ns(100,0,0,0,unchanged)&&unchanged==123,"zero grid refuses without output");
        require(!sbrec_sdk_utc_pts_ns(99,100,100,1000000,unchanged),"UTC underflow refuses");
        require(!sbrec_sdk_utc_pts_ns(INT64_MAX,0,1,1,unchanged),"PTS overflow refuses");
        require(!sbrec_sdk_utc_pts_ns(100,0,UINT64_MAX,1,unchanged),"unknown GStreamer PTS refuses");
        require(sbrec_sdk_utc_pts_ns(INT64_MAX,0,0,1,unchanged)&&unchanged==INT64_MAX,"exact signed nanosecond boundary");
        require(SbrecSdkTimecodeScaleMs==1 && SbrecSdkUtcQuantization100ns==10000,"owned SDK grid/UTC uncertainty bound");
        {
            uint64_t now = 1800000000;
            auto store = std::make_shared<sbrec::CredentialStore>([&] { return now; });
            Chain chain(store);
            PAwsCredentials value = nullptr;
            require(STATUS_FAILED(chain.read(&value)) && !value, "missing credentials fail closed");
            auto input = fake(now + 900);
            // Compute literal lengths, including no NULs, for fake-only fixtures.
            input.secretKeyBytes = static_cast<uint32_t>(strlen(input.secretKey));
            input.sessionTokenBytes = static_cast<uint32_t>(strlen(input.sessionToken));
            require(store->update(input) == SBREC_OK, "initial grant accepted");
            require(STATUS_SUCCEEDED(chain.read(&value)), "actual SDK auth reads initial grant");
            auto* borrowed = value;
            const auto firstExpiry = value->expiration;
            require(firstExpiry == input.expiresUnixSeconds * HUNDREDS_OF_NANOS_IN_A_SECOND, "expiry not replaced by infinity");
            require(store->update(input) == SBREC_OK && store->snapshots() == 1, "duplicate is idempotent");
            auto invalid = input; invalid.expiresUnixSeconds = now - 1;
            require(store->update(invalid) == SBREC_INVALID && store->snapshots() == 1, "expired replacement rejected without retention");
            invalid = input; invalid.sessionToken = nullptr;
            require(store->update(invalid) == SBREC_INVALID, "missing token rejected");
            invalid = input; invalid.accessKeyBytes = 129;
            require(store->update(invalid) == SBREC_INVALID, "oversized access key rejected before read");
            invalid = input; invalid.sessionToken = "bad token"; invalid.sessionTokenBytes = 9;
            require(store->update(invalid) == SBREC_INVALID, "malformed token rejected");
            invalid = input; invalid.expiresUnixSeconds = UINT64_MAX;
            require(store->update(invalid) == SBREC_INVALID, "overflow expiration rejected");
            now += 60; input.expiresUnixSeconds = now + 900;
            require(store->update(input) == SBREC_OK && store->snapshots() == 2, "replacement accepted");
            require(STATUS_SUCCEEDED(chain.read(&value)) && value != borrowed && borrowed->expiration == firstExpiry,
                "borrowed snapshot survives replacement");
            input.secretKey = "FAKE_REPLACEMENT_SECRET_NOT_A_VALID_KEY";
            input.secretKeyBytes = static_cast<uint32_t>(strlen(input.secretKey));
            input.sessionToken = "FAKE_REPLACEMENT_TOKEN_NOT_VALID";
            input.sessionTokenBytes = static_cast<uint32_t>(strlen(input.sessionToken));
            ++input.expiresUnixSeconds;
            require(store->update(input) == SBREC_OK && STATUS_SUCCEEDED(chain.read(&value))
                && value->secretKeyLen == input.secretKeyBytes && !memcmp(value->secretKey, input.secretKey, input.secretKeyBytes)
                && value->sessionTokenLen == input.sessionTokenBytes && !memcmp(value->sessionToken, input.sessionToken, input.sessionTokenBytes)
                && memcmp(borrowed->secretKey, input.secretKey, input.secretKeyBytes), "new key/token replace immutable old snapshot");
            now = input.expiresUnixSeconds;
            require(STATUS_FAILED(chain.read(&value)) && !value, "real expiry fails closed");
            ServiceCallContext context{};
            require(STATUS_FAILED(chain.auth.getStreamingTokenFn(chain.auth.customData, nullptr, STREAM_ACCESS_MODE_WRITE, &context)), "actual SDK streaming callback rejects expired credentials");
        }
        require(sbrec::CredentialStore::live() == 0, "SDK callback teardown releases provider");
        {
            std::atomic<uint64_t> now{1800000000};
            auto store = std::make_shared<sbrec::CredentialStore>([&] { return now.load(); });
            auto input = fake(now + 900); input.secretKeyBytes = static_cast<uint32_t>(strlen(input.secretKey)); input.sessionTokenBytes = static_cast<uint32_t>(strlen(input.sessionToken));
            require(store->update(input) == SBREC_OK, "concurrent initial grant");
            Chain chain(store);
            std::atomic<int> errors{0};
            std::vector<std::thread> readers;
            for (int i = 0; i < 4; ++i) readers.emplace_back([&] {
                for (int n = 0; n < 2000; ++n) {
                    PBYTE data = nullptr; UINT32 size = 0; UINT64 expiry = 0;
                    auto status = chain.auth.getSecurityTokenFn(chain.auth.customData, &data, &size, &expiry);
                    auto* value = reinterpret_cast<PAwsCredentials>(data);
                    if (STATUS_FAILED(status) || !value || size != value->size || expiry != value->expiration
                        || value->accessKeyIdLen != 20 || memcmp(value->accessKeyId, "FAKEACCESS00000000001", 20)) ++errors;
                }
            });
            for (size_t n = 1; n < sbrec::MaxSnapshots; ++n) {
                input.expiresUnixSeconds++;
                if (store->update(input) != SBREC_OK) ++errors;
            }
            for (auto& thread : readers) thread.join();
            require(errors == 0, "8000 concurrent SDK reads with replacements");
            input.expiresUnixSeconds++;
            require(store->update(input) == SBREC_LIMIT && store->snapshots() == sbrec::MaxSnapshots, "snapshot count bounded");
        }
        {
            bool failClock = false;
            auto store = std::make_shared<sbrec::CredentialStore>([&]() -> uint64_t { if (failClock) throw std::runtime_error("FAKE_SENSITIVE_EXCEPTION_MARKER"); return 1800000000; });
            auto input = fake(1800000900); input.secretKeyBytes = static_cast<uint32_t>(strlen(input.secretKey)); input.sessionTokenBytes = static_cast<uint32_t>(strlen(input.sessionToken));
            require(store->update(input) == SBREC_OK, "exception fixture");
            Chain chain(store); failClock = true;
            PAwsCredentials value = nullptr;
            require(STATUS_FAILED(chain.read(&value)) && !value, "callback exception contained");
            require(store->update(input) == SBREC_INTERNAL && store->snapshots() == 1, "update exception contained without retention");
        }
        // Actual GObject injection seam, with SDK callback ownership independent
        // of both the caller's numeric handle and the sink's binding.
        gst_init(nullptr, nullptr);
        GError* error = nullptr;
        GstPlugin* plugin = gst_plugin_load_file(argv[1], &error);
        require(plugin != nullptr && error == nullptr, "patched plugin loads");
        {
            GstElement* sink = gst_element_factory_make("kvssink", nullptr);
            require(sink != nullptr, "patched sink constructs without network");
            bool selected = true;
            require(!sbrec::injected_credentials(G_OBJECT(sink), selected) && !selected, "legacy path unchanged when not selected");
            uint64_t handle = 0;
            require(sbrec_credentials_create(&handle) == SBREC_OK, "opaque handle created");
            require(sbrec_credentials_bind(sink, handle) == SBREC_OK, "opaque handle bound in NULL");
            bool rejected = false;
            try { sbrec::injected_credentials(G_OBJECT(sink), selected); } catch (...) { rejected = true; }
            require(rejected && selected, "selected missing credentials cannot use legacy path");
            auto now = static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count());
            auto input = fake(now + 900); input.secretKeyBytes = static_cast<uint32_t>(strlen(input.secretKey)); input.sessionTokenBytes = static_cast<uint32_t>(strlen(input.sessionToken));
            require(sbrec_credentials_update(handle, &input) == SBREC_OK, "C ABI update");
            auto adapter = sbrec::injected_credentials(G_OBJECT(sink), selected);
            require(adapter && selected, "actual injection constructs memory adapter");
            require(sbrec_credentials_bind(sink, handle) == SBREC_ALREADY_BOUND, "cannot rebind selected sink");
            std::atomic<int> raceErrors{0};
            std::thread updater([&] {
                for (int n = 0; n < 2000; ++n) {
                    const auto code = sbrec_credentials_update(handle, &input);
                    if (code != SBREC_OK && code != SBREC_INVALID) ++raceErrors;
                }
            });
            const auto released = sbrec_credentials_release(handle);
            const auto twice = sbrec_credentials_release(handle);
            updater.join();
            require(released == SBREC_OK && twice == SBREC_INVALID, "caller release fenced");
            require(raceErrors == 0, "concurrent C ABI update/release is fenced");
            require(sbrec_credentials_update(handle, &input) == SBREC_INVALID, "stale handle cannot update");
            gst_object_unref(sink);
            require(sbrec::CredentialStore::live() == 1, "SDK adapter keeps snapshots after binding/caller release");
            adapter.reset();
            require(sbrec::CredentialStore::live() == 0, "last adapter release clears snapshots");
        }
        {
            std::array<uint64_t, sbrec::MaxProviders> handles{};
            for (auto& handle : handles) require(sbrec_credentials_create(&handle) == SBREC_OK, "provider slot allocated");
            uint64_t extra = 42;
            require(sbrec_credentials_create(&extra) == SBREC_LIMIT && extra == 0, "provider count bounded");
            for (auto handle : handles) require(sbrec_credentials_release(handle) == SBREC_OK, "provider slot released");
        }
        require(sbrec::CredentialStore::live() == 0, "no retained stores at exit");
        {
            // Invoke exports from the loaded DLL, not the copy of the same core
            // compiled into this test executable. The negative READY transition
            // reaches the actual patched selection BEFORE any SDK cloud operation.
            auto module = GetModuleHandleA("gstkvssink.dll");
            auto create = reinterpret_cast<decltype(&sbrec_credentials_create)>(GetProcAddress(module, "sbrec_credentials_create"));
            auto bind = reinterpret_cast<decltype(&sbrec_credentials_bind)>(GetProcAddress(module, "sbrec_credentials_bind"));
            auto release = reinterpret_cast<decltype(&sbrec_credentials_release)>(GetProcAddress(module, "sbrec_credentials_release"));
            auto update = reinterpret_cast<decltype(&sbrec_credentials_update)>(GetProcAddress(module, "sbrec_credentials_update"));
            require(create && bind && release && update, "actual plugin credential exports");
            GstElement* sink = gst_element_factory_make("kvssink", nullptr);
            g_object_set(sink, "log-config", nullptr, "credential-path", "", nullptr);
            uint64_t handle = 0;
            require(create(&handle) == SBREC_OK && bind(sink, handle) == SBREC_OK, "DLL provider injection");
            require(gst_element_set_state(sink, GST_STATE_READY) == GST_STATE_CHANGE_FAILURE, "patched NULL to READY rejects missing provider before AWS");
            gst_element_set_state(sink, GST_STATE_NULL);
            require(release(handle) == SBREC_OK, "DLL caller handle released");
            gst_object_unref(sink);
            sink = gst_element_factory_make("kvssink", nullptr);
            g_object_set(sink, "log-config", nullptr, "credential-path", "", nullptr);
            require(bind(sink, UINT64_MAX) == SBREC_INVALID, "invalid DLL handle explicitly binds unavailable auth");
            require(gst_element_set_state(sink, GST_STATE_READY) == GST_STATE_CHANGE_FAILURE, "invalid selected handle cannot fall back to ambient credentials");
            gst_element_set_state(sink, GST_STATE_NULL); gst_object_unref(sink);
            std::array<GstElement*, sbrec::MaxProviders> bindings{};
            for (auto& element : bindings) {
                require(create(&handle) == SBREC_OK, "DLL bounded store create");
                element = gst_element_factory_make("kvssink", nullptr);
                require(bind(element, handle) == SBREC_OK && release(handle) == SBREC_OK, "binding retains DLL store after caller release");
            }
            require(create(&handle) == SBREC_LIMIT, "caller cannot bypass live provider cap using retained bindings");
            for (auto* element : bindings) gst_object_unref(element);
            require(create(&handle) == SBREC_OK && release(handle) == SBREC_OK, "DLL teardown reclaims provider capacity");
        }
        checks += run_sdk_logging_tests();
        gst_object_unref(plugin);
        std::cout << "PASS checks=" << checks << " concurrent_reads=8000 max_snapshots=32 max_providers=8\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << "FAIL " << error.what() << '\n'; return 1; }
    catch (...) { std::cerr << "FAIL unexpected test exception\n"; return 1; }
}
