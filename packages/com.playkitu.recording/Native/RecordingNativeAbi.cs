using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Playkitu.Client;

#if PLAYTEST_RECORDING_SYNTHETIC_PROOF && PLAYTEST_RECORDING_NATIVE_PROOF
#error Synthetic and B0 native proof variants must remain mutually exclusive.
#endif

namespace Playkitu.Native
{
    // Exact B2c Cdecl ABI, Windows x64 default pack8. Native integer clocks and
    // identities never pass through floating point. No production start or
    // credential-update import exists in this structurally offline assembly.
    public static class RecordingNativeAbi
    {
        public const uint Busy = 2, NotReady = 4;
        [StructLayout(LayoutKind.Sequential)] public struct Clock
        { public ulong Qpc, Frequency, Unix100ns, Uncertainty100ns; }
        [StructLayout(LayoutKind.Sequential)] public struct Settings
        {
            public uint Size, Seconds, Width, Height, Fps, ObservationCapacity, StartupMs, FlushMs;
            public ulong EncodedByteLimit;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 37)] public byte[] SessionId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 37)] public byte[] RunId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 129)] public byte[] StreamName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)] public byte[] CaFile;
            public ulong ManagedTicks, ManagedFrequency, CorrespondenceQpc, CorrespondenceUncertainty100ns;
        }
        [StructLayout(LayoutKind.Sequential)] public struct Observation
        {
            public ulong Sequence;
            public uint Kind, Track, Flags, Result;
            public Clock Clock;
            public ulong PtsNs, SegmentStartNs, SegmentBaseNs, RunningNs, SdkPts100ns;
            public ulong Upload, TimecodeMs, Bytes, Epoch;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 129)] public byte[] Fragment;
        }
        [StructLayout(LayoutKind.Sequential)] public struct Status
        {
            public uint Size, State, Reason, Degraded;
            public ulong Observations, DroppedObservations, EncodedBytes, AcceptedVideo, AcceptedAudio;
            public ulong ReceivedAcks, PersistedAcks, Resets, Gaps, CredentialRevision, CredentialResult;
            public Clock FirstSource, LastSource;
            public ulong SourceFrames, FirstVideoRunningNs, FirstVideoSdk100ns, LastVideoRunningNs, LastVideoSdk100ns;
            public ulong FirstAudioRunningNs, FirstAudioSdk100ns, LastAudioRunningNs, LastAudioSdk100ns;
            public uint WorkerDone, CallbacksQuiescent;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)] public struct FrameClock
        {
            public uint Size, Version, Kind, Flags, Track, Reason, SourceSegmentSeq, EncodedSegmentSeq;
            public ulong Sequence, FrameOrdinal, ClockEpoch, ProducerEpoch, QpcBefore, QpcAfter, QpcFrequency, GstClockTimeNs, PipelineBaseTimeNs, ClockResolutionNs, CalibrationInternalNs, CalibrationExternalNs, CalibrationRateNumerator, CalibrationRateDenominator, PadArrivalQpc, EstimatedScheduleQpc, ScheduleUncertaintyNs, SourcePtsNs, SourceDurationNs, SourceRunningNs, SourceSegmentStartNs, SourceSegmentBaseNs, EncodedPtsNs, EncodedRunningNs, EncodedSegmentStartNs, EncodedSegmentBaseNs, SdkInputRunningNs, SdkPts100ns;
            public RecordingFrameClock ToRecord() => new RecordingFrameClock { Size = Size, Version = Version, Kind = Kind, Flags = Flags, Track = Track, Reason = Reason, SourceSegmentSeq = SourceSegmentSeq, EncodedSegmentSeq = EncodedSegmentSeq, Sequence = Sequence, FrameOrdinal = FrameOrdinal, ClockEpoch = ClockEpoch, ProducerEpoch = ProducerEpoch, QpcBefore = QpcBefore, QpcAfter = QpcAfter, QpcFrequency = QpcFrequency, GstClockTimeNs = GstClockTimeNs, PipelineBaseTimeNs = PipelineBaseTimeNs, ClockResolutionNs = ClockResolutionNs, CalibrationInternalNs = CalibrationInternalNs, CalibrationExternalNs = CalibrationExternalNs, CalibrationRateNumerator = CalibrationRateNumerator, CalibrationRateDenominator = CalibrationRateDenominator, PadArrivalQpc = PadArrivalQpc, EstimatedScheduleQpc = EstimatedScheduleQpc, ScheduleUncertaintyNs = ScheduleUncertaintyNs, SourcePtsNs = SourcePtsNs, SourceDurationNs = SourceDurationNs, SourceRunningNs = SourceRunningNs, SourceSegmentStartNs = SourceSegmentStartNs, SourceSegmentBaseNs = SourceSegmentBaseNs, EncodedPtsNs = EncodedPtsNs, EncodedRunningNs = EncodedRunningNs, EncodedSegmentStartNs = EncodedSegmentStartNs, EncodedSegmentBaseNs = EncodedSegmentBaseNs, SdkInputRunningNs = SdkInputRunningNs, SdkPts100ns = SdkPts100ns };
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)] public struct ExternalInput
        {
            public uint Size, Version, Track, Provenance, Width, Height, StrideBytes, SampleFrames;
            public IntPtr Data;
            public uint Bytes, Channels, Rate, Reserved;
            public ulong CaptureQpc, DurationNs;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)] public struct ExternalStatus
        {
            public uint Size, Version, Ready, Reserved;
            public ulong OriginQpc, Frequency, AcceptedVideo, AcceptedAudio, DroppedVideo, DroppedAudio, InvalidInputs, ProcessContentionReturns, ObservationDrops;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)] public struct ExternalObservation
        {
            public uint Size, Version, Track, Provenance, Result, Discontinuity;
            public ulong Sequence, CaptureQpc, SubmissionQpc, PtsNs, DurationNs, Bytes;
        }
        public static RecordingTimingObservation ToTiming(Guid run, Observation observation)
        {
            string kind;
            switch (observation.Kind)
            {
                case 1: kind = "submitted"; break; case 2: kind = "ack"; break;
                case 3: kind = "reset"; break; case 4: kind = "gap"; break;
                case 10: kind = "source"; break; case 11: kind = "encoded"; break;
                default: return null; // State observations belong to health, not media/capture ranges.
            }
            int end = observation.Fragment == null ? 0 : Array.IndexOf(observation.Fragment, (byte)0);
            if (end < 0) throw new RecordingFailure("invalid_native_observation");
            // Accepted ABI1 source_probe fills PTS/clock only. Its zero-initialized
            // segment/running slots are unprovided, not measured zeros. Source
            // transforms come from complete ABI2 clock groups; encoded/submitted
            // observations and independently supplied timing may legitimately be0.
            bool sourceTransformUnavailable = observation.Kind == 10;
            return new RecordingTimingObservation { RunId = run, Kind = kind, Sequence = observation.Sequence, Epoch = observation.Epoch,
                Track = observation.Track == 1 ? "video" : observation.Track == 2 ? "audio" : null,
                Flags = observation.Flags, Result = observation.Result, Bytes = observation.Bytes,
                PtsNs = observation.PtsNs, SegmentStartNs = sourceTransformUnavailable ? (ulong?)null : observation.SegmentStartNs,
                SegmentBaseNs = sourceTransformUnavailable ? (ulong?)null : observation.SegmentBaseNs,
                RunningNs = sourceTransformUnavailable ? (ulong?)null : observation.RunningNs, SdkPts100ns = observation.SdkPts100ns,
                Upload = observation.Upload, TimecodeMs = observation.TimecodeMs,
                Fragment = end == 0 ? null : Encoding.ASCII.GetString(observation.Fragment, 0, end),
                ObservationQpc = observation.Clock.Qpc, ObservationFrequency = observation.Clock.Frequency,
                ObservationUnix100ns = observation.Clock.Unix100ns, ObservationUncertainty100ns = observation.Clock.Uncertainty100ns };
        }

        // Calls only clock/create/status/drain/stop/release on a CREATED run. No
        // native worker, SDK, pipeline, device or scanner is ever started here.
        public sealed class Smoke
        {
            private sbrec_runner_external_v1_info_fn sbrec_runner_external_v1_info;
            private sbrec_runner_enable_external_v1_fn sbrec_runner_enable_external_v1;
            private sbrec_runner_external_status_v1_fn sbrec_runner_external_status_v1;
            private sbrec_runner_push_external_v1_fn sbrec_runner_push_external_v1;
            private sbrec_runner_drain_external_v1_fn sbrec_runner_drain_external_v1;
            private sbrec_runner_clock_v2_info_fn sbrec_runner_clock_v2_info;
            private sbrec_runner_enable_clock_v2_fn sbrec_runner_enable_clock_v2;
            private sbrec_runner_drain_clock_v2_fn sbrec_runner_drain_clock_v2;
            private sbrec_runner_abi_version_fn sbrec_runner_abi_version;
            private sbrec_runner_clock_fn sbrec_runner_clock;
            private sbrec_runner_create_fn sbrec_runner_create;
            private sbrec_runner_status_fn sbrec_runner_status;
            private sbrec_runner_drain_observations_fn sbrec_runner_drain_observations;
            private sbrec_runner_request_stop_fn sbrec_runner_request_stop;
            private sbrec_runner_release_fn sbrec_runner_release;
            public bool TryGetFunction<T>(string name,out T function) where T : Delegate
            {
                IntPtr address=GetProcAddress(module,name);
                function=address==IntPtr.Zero?null:Marshal.GetDelegateForFunctionPointer<T>(address);
                return address!=IntPtr.Zero;
            }
            public T GetFunction<T>(string name) where T : Delegate
            {
                IntPtr address = GetProcAddress(module, name);
                if (address == IntPtr.Zero) throw new RecordingFailure("native_export_missing");
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }
            public Clock ReadClock()
            {
                if (sbrec_runner_clock(out var sample) != 0 || sample.Frequency == 0) throw new RecordingFailure("native_clock_failed");
                return sample;
            }
            // Reuse the existing no-argument version-query delegate for IL2CPP.
            // Legacy DLLs hardcode a region and must never pass public trust checks.
            public bool SupportsTrustedRegion() => module != IntPtr.Zero
                && TryGetFunction<sbrec_runner_abi_version_fn>("sbrec_runner_trusted_region_version", out var query)
                && query() == 1;
            public uint Abi, CreatedState, FinalState, ReleaseResult = 2, Drained, ClockVersion, ClockRecordBytes;
            public ulong Handle => handle;
            public Clock Sample;
            public ulong ManagedBeforeTicks, ManagedAfterTicks, ManagedFrequencyHz, CorrespondenceUncertainty100ns;
            public string Error, LoadFailure, LoadedModule;
            private IntPtr module;
            private ulong handle;
            public bool Released => handle == 0 && module == IntPtr.Zero;
            public void Load(string runtime, uint expectedAbi = 1, uint expectedClockVersion = 2, uint expectedClockBytes = 256, bool external = false)
            {
                try
                {
                    string directory = Path.GetFullPath(Path.Combine(runtime, "bin"));
                    string dll = Path.Combine(directory, "PlaytestRecordingNativeProof.dll");
                    if (!File.Exists(dll)) throw new RecordingFailure("native_missing");
                    // Dependency lookup is scoped to this DLL directory and Windows defaults;
                    // no process-wide loader policy or directory cookie is changed.
                    if ((module = LoadLibraryExW(dll, IntPtr.Zero, 0x1100)) == IntPtr.Zero) throw new RecordingFailure("native_load_failed");
                    var loaded = new StringBuilder(32768);
                    if (GetModuleFileNameW(module, loaded, loaded.Capacity) == 0) throw new RecordingFailure("native_module_mismatch");
                    LoadedModule = Path.GetFullPath(loaded.ToString());
                    if (!string.Equals(LoadedModule, dll, StringComparison.OrdinalIgnoreCase)) throw new RecordingFailure("native_module_mismatch");
                    sbrec_runner_abi_version = GetFunction<sbrec_runner_abi_version_fn>("sbrec_runner_abi_version");
                    sbrec_runner_clock = GetFunction<sbrec_runner_clock_fn>("sbrec_runner_clock");
                    sbrec_runner_create = GetFunction<sbrec_runner_create_fn>("sbrec_runner_create");
                    sbrec_runner_status = GetFunction<sbrec_runner_status_fn>("sbrec_runner_status");
                    sbrec_runner_drain_observations = GetFunction<sbrec_runner_drain_observations_fn>("sbrec_runner_drain_observations");
                    sbrec_runner_request_stop = GetFunction<sbrec_runner_request_stop_fn>("sbrec_runner_request_stop");
                    sbrec_runner_release = GetFunction<sbrec_runner_release_fn>("sbrec_runner_release");
                    if (external) {
                        sbrec_runner_external_v1_info = GetFunction<sbrec_runner_external_v1_info_fn>("sbrec_runner_external_v1_info");
                        sbrec_runner_enable_external_v1 = GetFunction<sbrec_runner_enable_external_v1_fn>("sbrec_runner_enable_external_v1");
                        sbrec_runner_external_status_v1 = GetFunction<sbrec_runner_external_status_v1_fn>("sbrec_runner_external_status_v1");
                        sbrec_runner_push_external_v1 = GetFunction<sbrec_runner_push_external_v1_fn>("sbrec_runner_push_external_v1");
                        sbrec_runner_drain_external_v1 = GetFunction<sbrec_runner_drain_external_v1_fn>("sbrec_runner_drain_external_v1");
                    } else {
                        sbrec_runner_clock_v2_info = GetFunction<sbrec_runner_clock_v2_info_fn>("sbrec_runner_clock_v2_info");
                        sbrec_runner_enable_clock_v2 = GetFunction<sbrec_runner_enable_clock_v2_fn>("sbrec_runner_enable_clock_v2");
                        sbrec_runner_drain_clock_v2 = GetFunction<sbrec_runner_drain_clock_v2_fn>("sbrec_runner_drain_clock_v2");
                    }
                    Abi = sbrec_runner_abi_version();
                    if (Abi != expectedAbi) throw new RecordingFailure("native_abi_mismatch");
                    if (Marshal.SizeOf<Observation>() != 264 || (!external && (Marshal.SizeOf<FrameClock>() != 256
                        || sbrec_runner_clock_v2_info(out ClockVersion, out ClockRecordBytes) != 0 || ClockVersion != expectedClockVersion || ClockRecordBytes != expectedClockBytes)))
                        throw new RecordingFailure("native_clock_capability_mismatch");
                    ManagedBeforeTicks = (ulong)Stopwatch.GetTimestamp();
                    uint clockResult = sbrec_runner_clock(out Sample);
                    ManagedAfterTicks = (ulong)Stopwatch.GetTimestamp(); ManagedFrequencyHz = (ulong)Stopwatch.Frequency;
                    if (clockResult != 0 || Sample.Frequency == 0) throw new RecordingFailure("native_clock_failed");
                    CorrespondenceUncertainty100ns = checked(Sample.Uncertainty100ns + (ManagedAfterTicks - ManagedBeforeTicks) * 10000000 / ManagedFrequencyHz);
                    if (sbrec_runner_create(out handle) != 0 || handle == 0) throw new RecordingFailure("native_create_failed");
                    var state = new Status { Size = (uint)Marshal.SizeOf<Status>() };
                    if (sbrec_runner_status(handle, ref state) != 0) throw new RecordingFailure("native_status_failed");
                    CreatedState = state.State;
                    if (external) {
                        if (Marshal.SizeOf<ExternalInput>() != 72 || Marshal.SizeOf<ExternalStatus>() != 88 || Marshal.SizeOf<ExternalObservation>() != 72
                            || sbrec_runner_external_v1_info(out uint version, out uint input, out uint status, out uint observation) != 0
                            || version != 1 || input != 72 || status != 88 || observation != 72)
                            throw new RecordingFailure("native_external_abi_mismatch");
                        if (sbrec_runner_enable_external_v1(handle, 1) != 0) throw new RecordingFailure("native_external_enable_failed");
                    } else {
                        if (sbrec_runner_enable_clock_v2(handle, 2, 256, 64, 1000) != 0) throw new RecordingFailure("native_clock_enable_failed");
                        var clocks = new FrameClock[4];
                        if (sbrec_runner_drain_clock_v2(handle, clocks, 256, 4, out uint written, out ulong lost) != 0 || written != 0 || lost != 0)
                            throw new RecordingFailure("native_created_clock_not_empty");
                    }
                    var observations = new Observation[4];
                    if (sbrec_runner_drain_observations(handle, observations, 4, out Drained) != 0 || Drained != 0)
                        throw new RecordingFailure("native_created_not_empty");
                }
                catch (Exception error) { Error = error is RecordingFailure ? error.Message : "native_unavailable"; LoadFailure = error is DllNotFoundException ? "dll_missing" : error is EntryPointNotFoundException ? "entry_missing" : error is BadImageFormatException ? "bad_image" : error is MarshalDirectiveException ? "marshal" : "other"; }
            }
            // Re-measure AFTER RecordingTiming anchors session elapsed. These
            // fields bracket the actual native call; optional startTicks is not an origin.
            public RecordingTimingObservation MeasureClock(Guid run, RecordingTiming timing, IRecordingClock clock)
            {
                long beforeElapsed = timing.ElapsedNs(clock.Seconds);
                ulong before = (ulong)Stopwatch.GetTimestamp();
                uint result = sbrec_runner_clock(out var sample);
                ulong after = (ulong)Stopwatch.GetTimestamp();
                long afterElapsed = timing.ElapsedNs(clock.Seconds);
                if (result != 0 || sample.Frequency == 0 || sample.Uncertainty100ns == ulong.MaxValue || after < before)
                    throw new RecordingFailure("native_clock_failed");
                Sample = sample; ManagedBeforeTicks = before; ManagedAfterTicks = after; ManagedFrequencyHz = (ulong)Stopwatch.Frequency;
                // Ceiling, not truncation; native UTC/QPC uncertainty and managed
                // call width stay separately present in the raw observation.
                CorrespondenceUncertainty100ns = checked(sample.Uncertainty100ns +
                    (ulong)decimal.Ceiling((decimal)(after - before) * 10000000 / ManagedFrequencyHz));
                return new RecordingTimingObservation { RunId = run, Kind = "clock", ObservationQpc = sample.Qpc,
                    ObservationFrequency = sample.Frequency, ObservationUnix100ns = sample.Unix100ns,
                    ObservationUncertainty100ns = sample.Uncertainty100ns, ManagedTicks = before, ManagedAfterTicks = after,
                    ManagedFrequencyHz = ManagedFrequencyHz, CorrespondenceQpc = sample.Qpc,
                    CorrespondenceUncertainty100ns = CorrespondenceUncertainty100ns,
                    ManagedBeforeElapsedNs = beforeElapsed, ManagedAfterElapsedNs = afterElapsed };
            }
            public Status Poll()
            {
                var state = new Status { Size = (uint)Marshal.SizeOf<Status>() };
                if (sbrec_runner_status(handle, ref state) != 0) throw new RecordingFailure("native_status_failed");
                return state;
            }
            public Observation[] Drain()
            {
                var rows = new Observation[64];
                if (sbrec_runner_drain_observations(handle, rows, 64, out uint count) != 0 || count > 64) throw new RecordingFailure("native_drain_failed");
                Array.Resize(ref rows, (int)count); return rows;
            }
            public FrameClock[] DrainClocks(out ulong dropped)
            {
                var rows = new FrameClock[64];
                if (sbrec_runner_drain_clock_v2(handle, rows, 256, 64, out uint count, out dropped) != 0 || count > 64)
                    throw new RecordingFailure("native_clock_drain_failed");
                Array.Resize(ref rows, (int)count); return rows;
            }
            private ExternalStatus lastExternalStatus;
            public ExternalStatus PollExternal()
            {
                var status = new ExternalStatus { Size = 88, Version = 1 };
                uint result = sbrec_runner_external_status_v1(handle, ref status);
                if (result == Busy) return lastExternalStatus; // One observation attempt; never spin on native pressure.
                if (result != 0) throw new RecordingFailure("native_external_status_failed");
                lastExternalStatus = status; return status;
            }
            public uint Push(ref ExternalInput input) => sbrec_runner_push_external_v1(handle, ref input);
            public ExternalObservation[] DrainExternal()
            {
                var rows = new ExternalObservation[64];
                uint result = sbrec_runner_drain_external_v1(handle, rows, 64, out uint count);
                if (result == Busy) return Array.Empty<ExternalObservation>();
                if (result != 0 || count > 64) throw new RecordingFailure("native_external_drain_failed");
                Array.Resize(ref rows, (int)count); return rows;
            }
            public void RequestStop() { if (handle != 0) sbrec_runner_request_stop(handle); }
            public bool StopAndRelease()
            {
                if (handle != 0)
                {
                    if (sbrec_runner_request_stop(handle) != 0) { Error = "native_stop_failed"; return false; }
                    var state = new Status { Size = (uint)Marshal.SizeOf<Status>() };
                    if (sbrec_runner_status(handle, ref state) != 0) return false;
                    FinalState = state.State;
                    if (state.State != 4 || state.WorkerDone == 0 || state.CallbacksQuiescent == 0) return false;
                    ReleaseResult = sbrec_runner_release(handle);
                    if (ReleaseResult != 0) return false; // RUN_BUSY retains both generation and loaded DLL.
                    handle = 0;
                }
                if (module != IntPtr.Zero) { FreeLibrary(module); module = IntPtr.Zero; }
                return true;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_external_v1_info_fn(out uint version, out uint input, out uint status, out uint observation);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_enable_external_v1_fn(ulong handle, uint version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_external_status_v1_fn(ulong handle, ref ExternalStatus status);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_push_external_v1_fn(ulong handle, ref ExternalInput input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_drain_external_v1_fn(ulong handle, [Out] ExternalObservation[] rows, uint capacity, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_clock_v2_info_fn(out uint version, out uint bytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_enable_clock_v2_fn(ulong handle, uint version, uint bytes, uint capacity, uint intervalMs);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_drain_clock_v2_fn(ulong handle, [Out] FrameClock[] output, uint bytes, uint capacity, out uint count, out ulong dropped);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_abi_version_fn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_clock_fn(out Clock clock);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_create_fn(out ulong handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_status_fn(ulong handle, ref Status status);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_drain_observations_fn(ulong handle, [Out] Observation[] observations, uint capacity, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_request_stop_fn(ulong handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint sbrec_runner_release_fn(ulong handle);
        [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder file, int capacity);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32")] private static extern bool FreeLibrary(IntPtr module);

    }
}

