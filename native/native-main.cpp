// The console and Unity C ABI adapter execute exactly the same native operations.
int run_native_proof(int argc, wchar_t** argv);
int wmain(int argc, wchar_t** argv) { return run_native_proof(argc, argv); }
