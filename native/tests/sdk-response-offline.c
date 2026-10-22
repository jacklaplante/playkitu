// Compile the actual pinned/patched Response.c for the test with only its two
// transport calls intercepted. Production cproducer compiles its own Response.c
// unchanged by these macros. No socket, HTTP listener or curl perform can run.
#include "Include_i.h"
#undef LOG_CLASS
CURLcode sbrec_offline_curl_perform(CURL*);
CURLcode sbrec_offline_curl_getinfo(CURL*, CURLINFO, ...);
#define curl_easy_perform sbrec_offline_curl_perform
#define curl_easy_getinfo sbrec_offline_curl_getinfo
#include "Response.c"
