// Real SDK signing plus real HTTP-failure diagnostics, with fake credentials and
// transport-only interception. Captured messages stay in test memory: even the
// negative proof of the original bug never writes fake credential values to logs.
#include "Include_i.h"
#include "Common/AwsV4Signer.h"
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>

namespace {
std::string messages;
bool truncated = false;
int transportCalls = 0;
void capture(UINT32 level, const PCHAR, const PCHAR format, ...) {
    if (level < loggerGetLogLevel()) return;
    char buffer[8192];
    va_list args; va_start(args, format);
    const int size = vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    if (size < 0 || size >= static_cast<int>(sizeof(buffer)) || messages.size() + size > 65536) { truncated = true; return; }
    messages.append(buffer, static_cast<size_t>(size)); messages += '\n';
}
struct CaptureScope {
    logPrintFunc previous = globalCustomLogPrintFn;
    UINT32 level = loggerGetLogLevel();
    CaptureScope() { globalCustomLogPrintFn = capture; }
    ~CaptureScope() { globalCustomLogPrintFn = previous; SET_LOGGER_LOG_LEVEL(level); }
};
void check(bool value, const char* label, int& count) {
    if (!value) throw std::runtime_error(label);
    ++count;
}
std::string wireHeaders(PRequestInfo request) {
    std::string result;
    PSingleListNode node = nullptr;
    singleListGetHeadNode(request->pRequestHeaders, &node);
    while (node) {
        UINT64 data = 0; singleListGetNodeData(node, &data);
        auto* header = reinterpret_cast<PRequestHeader>(data);
        result.append(header->pName, header->nameLen); result += ": ";
        result.append(header->pValue, header->valueLen); result += '\n';
        singleListGetNextNode(node, &node);
    }
    return result;
}
struct Fixture {
    PAwsCredentials credentials = nullptr;
    PRequestInfo request = nullptr;
    CurlApiCallbacks callbacks{};
    CurlRequest curlRequest{};
    CurlResponse response{};
    ~Fixture() {
        if (response.pRequestHeaders) curl_slist_free_all(response.pRequestHeaders);
        if (response.pCurl) curl_easy_cleanup(response.pCurl);
        freeRequestInfo(&request); freeAwsCredentials(&credentials);
    }
};
}

extern "C" CURLcode sbrec_offline_curl_perform(CURL*) { ++transportCalls; return CURLE_OK; }
extern "C" CURLcode sbrec_offline_curl_getinfo(CURL*, CURLINFO field, ...) {
    va_list args; va_start(args, field);
    if (field == CURLINFO_RESPONSE_CODE) *va_arg(args, long*) = 403;
    else if (field == CURLINFO_EFFECTIVE_URL) *va_arg(args, char**) = const_cast<char*>("https://offline.invalid/test?debug=FAKE_QUERY_TOKEN");
    else { va_end(args); return CURLE_UNKNOWN_OPTION; }
    va_end(args); return CURLE_OK;
}

int run_sdk_logging_tests() {
    int count = 0;
    CaptureScope captureScope;
    messages.clear(); truncated = false; transportCalls = 0;
    SET_LOGGER_LOG_LEVEL(LOG_LEVEL_DEBUG);
    Fixture f;
    check(STATUS_SUCCEEDED(createAwsCredentials(const_cast<PCHAR>("FAKEACCESS_LOGGING_ONLY"), 0,
        const_cast<PCHAR>("FAKE_SECRET_LOGGING_NEVER_VALID"), 0,
        const_cast<PCHAR>("FAKE_SESSION_TOKEN_LOGGING_ONLY"), 0,
        1792066500ULL * HUNDREDS_OF_NANOS_IN_A_SECOND, &f.credentials)), "offline signing credentials", count);
    check(STATUS_SUCCEEDED(createRequestInfo(const_cast<PCHAR>("https://offline.invalid/test?debug=FAKE_QUERY_TOKEN"), const_cast<PCHAR>("{}"),
        const_cast<PCHAR>("us-east-2"), nullptr, nullptr, nullptr, SSL_CERTIFICATE_TYPE_NOT_SPECIFIED,
        const_cast<PCHAR>("offline-logging-test"), HUNDREDS_OF_NANOS_IN_A_SECOND, HUNDREDS_OF_NANOS_IN_A_SECOND, 0, 0, f.credentials, &f.request)), "offline signing request", count);
    f.request->currentTime = 1792065600ULL * HUNDREDS_OF_NANOS_IN_A_SECOND;
    check(STATUS_SUCCEEDED(setRequestHeader(f.request, const_cast<PCHAR>("Cookie"), 0, const_cast<PCHAR>("FAKE_COOKIE_MARKER"), 0)), "fake cookie request header", count);
    check(STATUS_SUCCEEDED(setRequestHeader(f.request, const_cast<PCHAR>("X-Diagnostic-Test"), 0, const_cast<PCHAR>("FAKE_OTHER_HEADER"), 0)), "unknown request header", count);
    check(STATUS_SUCCEEDED(signAwsRequestInfo(f.request)), "actual SDK signing succeeds", count);
    const auto before = wireHeaders(f.request);
    check(before.find("FAKEACCESS_LOGGING_ONLY") != std::string::npos && before.find("FAKE_SESSION_TOKEN_LOGGING_ONLY") != std::string::npos
        && before.find("Signature=") != std::string::npos, "actual signed headers retain credentials/signature for transport", count);
    char digest[65]{};
    check(STATUS_SUCCEEDED(hexEncodedSha256(reinterpret_cast<PBYTE>(const_cast<char*>(before.data())), static_cast<UINT32>(before.size()), digest)), "signed header digest", count);
    // Fixed request/date/key fixtures: this digest is recorded from the unpatched
    // SDK before redaction, then asserted to prove signing/header bytes unchanged.
    const char* expected = "4098242c287a43328c0ee55d84b06cb5e93e325eb33335f01b6de2a7b709554c";
    std::printf("signed_headers_sha256=%s\n", digest);
    if (*expected) check(!strcmp(digest, expected), "signed wire header golden digest unchanged", count);
    check(!truncated && messages.find("Appending header") != std::string::npos, "DEBUG signing diagnostic actually exercised", count);
    const bool signingClean = messages.find("FAKE") == std::string::npos;
    messages.clear();
    // WARN logging exercises the actual HTTP-failure block independently of DEBUG.
    SET_LOGGER_LOG_LEVEL(LOG_LEVEL_WARN);
    f.curlRequest.requestInfo = *f.request; // borrowed for this synchronous test
    f.curlRequest.pCurlApiCallbacks = &f.callbacks;
    strcpy_s(f.curlRequest.streamName, "offline-stream");
    f.response.pCurlRequest = &f.curlRequest;
    f.response.pCurl = curl_easy_init();
    f.response.callInfo.responseData = const_cast<PCHAR>("FAKE_SESSION_TOKEN_REFLECTED_RESPONSE");
    check(f.response.pCurl != nullptr, "offline curl handle", count);
    check(STATUS_SUCCEEDED(curlCompleteSync(&f.response)) && transportCalls == 1, "actual HTTP failure function uses intercepted transport only", count);
    check(f.response.callInfo.httpStatus == 403 && f.response.callInfo.callResult == SERVICE_CALL_FORBIDDEN, "HTTP error result semantics preserved", count);
    check(!truncated && messages.find("HTTP Error 403") != std::string::npos, "HTTP failure diagnostic actually exercised at WARN", count);
    const bool httpClean = messages.find("FAKE") == std::string::npos && messages.find("Signature=") == std::string::npos;
    std::printf("redaction_debug_clean=%d redaction_http_clean=%d\n", signingClean, httpClean);
    check(wireHeaders(f.request) == before, "failure diagnostics do not mutate signed headers", count);
    bool tokenOnWire = false, signatureOnWire = false;
    for (auto* h = f.response.pRequestHeaders; h; h = h->next) {
        tokenOnWire |= strstr(h->data, "FAKE_SESSION_TOKEN_LOGGING_ONLY") != nullptr;
        signatureOnWire |= strstr(h->data, "Signature=") != nullptr;
    }
    check(tokenOnWire && signatureOnWire, "actual curl header list still contains required authentication", count);
    check(signingClean, "DEBUG signing log must omit credential/header values", count);
    check(httpClean, "HTTP failure log must omit header values and reflected secrets", count);
    std::printf("sdk_logging_checks=%d intercepted_transport_calls=%d\n", count, transportCalls);
    return count;
}
