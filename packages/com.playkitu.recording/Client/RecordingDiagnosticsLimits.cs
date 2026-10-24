namespace Playkitu.Client
{
    // Shared with the issuer via a linked source file: protocol/storage ceilings,
    // not permission to extend media or increase upload pacing. E34 four-hour diagnostics.
    public static class RecordingDiagnosticsLimits
    {
        public const int SessionSeconds = 14400, MetadataFinalizationSeconds = 60;
        public const int ObjectsPerKind = 4096, ObjectsPerSession = 8192;
        public const long StoredBytesPerSession = 256L * 1024 * 1024;
        public const long AttemptBytesPerSession = 512L * 1024 * 1024;
        public const int FinalObjectsReserve = 16, FinalPerKindReserve = 2;
        public const long FinalStoredBytesReserve = 2L * 1024 * 1024;
        public const int MaximumFramedAttemptBytes = 262144;
    }
}
