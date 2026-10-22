using Playreel.Native;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Playreel.Client;

namespace Playreel.Producer
{
    // Test seam intercepts the managed boundary only. Fake credentials are never
    // sent to the real native plugin. Production networking stays native-owned.
    public interface IExternalNativeRun
    {
        RecordingNativeAbi.Clock ReadClock();
        RecordingNativeAbi.ExternalStatus PollExternal();
        uint Push(ref RecordingNativeAbi.ExternalInput input);
        RecordingNativeAbi.ExternalObservation[] DrainExternal();
    }
    public interface IRecordingNativeRun
    {
        string Error { get; }
        void Load();
        RecordingTimingObservation Measure(Guid run, RecordingTiming timing, IRecordingClock clock);
        uint Start(RecordingNativeAbi.Settings settings, RecordingMedia media);
        uint Update(RecordingMedia media);
        RecordingNativeAbi.Status Poll();
        RecordingNativeAbi.Observation[] Drain();
        RecordingNativeAbi.FrameClock[] DrainClocks(out ulong dropped);
        void RequestStop();
        bool StopAndRelease();
    }
    public sealed class ProductionNativeRun : IRecordingNativeRun, IExternalNativeRun
    {
        private readonly string runtime;
        private readonly bool external;
        private readonly RecordingNativeAbi.Smoke owner = new RecordingNativeAbi.Smoke();
        private string configError;
        public string Error => configError ?? owner.Error;
        private readonly RecordingRunProfile profile;
        public ProductionNativeRun(string runtime, bool external = false, RecordingRunProfile profile = null) { this.runtime = runtime; this.external = external; this.profile=profile; }
        private StartFunction start;
        private UpdateFunction update;
        public void Load()
        {
            owner.Load(runtime, external: external);
            if (owner.Error == null && !owner.SupportsTrustedRegion()) {
                configError = "native_region_unsupported";
                owner.StopAndRelease();
                return;
            }
            if (owner.Error == null) { start = owner.GetFunction<StartFunction>("sbrec_runner_start"); update = owner.GetFunction<UpdateFunction>("sbrec_runner_update_credentials"); }
            if(owner.Error==null && profile!=null) {
                if(owner.TryGetFunction<ConfigureFunction>("sbrec_runner_configure_media_v1",out var configure)){
                    var c=new MediaConfig(profile);
                    if(configure(owner.Handle,ref c)!=0)configError="native_config_rejected";
                } else {
                    // D5's old runner can truthfully honor its certified D4 defaults.
                    // Never silently apply Low/new settings to an older unpaced SDK.
                    string producer=Path.Combine(runtime,"plugins","gstkvssink.dll"),hash=null;
                    if(File.Exists(producer))using(var algorithm=SHA256.Create())using(var stream=File.OpenRead(producer))
                        hash=BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-","");
                    if(!profile.UsesLegacyNativeDefaults||hash!="F385F0665E10388AAE87683771872D9CBA50C69EFCA0A9E627468CEC1456AA57")
                        configError="native_config_unsupported";
                }
            }
        }
        public RecordingTimingObservation Measure(Guid run, RecordingTiming timing, IRecordingClock clock) => owner.MeasureClock(run, timing, clock);
        public uint Start(RecordingNativeAbi.Settings settings, RecordingMedia media)
        { using (var bytes = new TemporaryMediaCredentials(media)) return start(owner.Handle, ref settings, ref bytes.Value); }
        public uint Update(RecordingMedia media)
        { using (var bytes = new TemporaryMediaCredentials(media)) return update(owner.Handle, ref bytes.Value); }
        public RecordingNativeAbi.Status Poll() => owner.Poll();
        public RecordingNativeAbi.Observation[] Drain() => owner.Drain();
        public RecordingNativeAbi.FrameClock[] DrainClocks(out ulong dropped) => owner.DrainClocks(out dropped);
        public RecordingNativeAbi.Clock ReadClock() => owner.ReadClock();
        public RecordingNativeAbi.ExternalStatus PollExternal() => owner.PollExternal();
        public uint Push(ref RecordingNativeAbi.ExternalInput input) => owner.Push(ref input);
        public RecordingNativeAbi.ExternalObservation[] DrainExternal() => owner.DrainExternal();
        public void RequestStop() => owner.RequestStop();
        public bool StopAndRelease() => owner.StopAndRelease();
        [StructLayout(LayoutKind.Sequential)] private struct MediaConfig
        {
            public uint Size,Version,Encoder,VideoKbps,MaxVideoKbps,AudioBps,GopFrames,VideoQueue,AudioQueue,InputVideo,InputAudio,MediaBps,Credit,Read,Wait,Slice;
            public MediaConfig(RecordingRunProfile p){
                Size=64;Version=1;Encoder=1;VideoKbps=(uint)p.VideoKbps;MaxVideoKbps=(uint)p.MaximumVideoKbps;AudioBps=(uint)p.AudioBitsPerSecond;GopFrames=(uint)p.GopFrames;
                VideoQueue=(uint)p.VideoQueue;AudioQueue=(uint)p.AudioQueue;InputVideo=(uint)p.InputVideoQueue;InputAudio=(uint)p.InputAudioQueue;
                MediaBps=(uint)p.MediaBytesPerSecond;Credit=(uint)p.CreditBytes;Read=(uint)p.ReadQuantumBytes;Wait=(uint)p.SenderWaitMilliseconds;Slice=(uint)p.SenderSliceMilliseconds;
            }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ConfigureFunction(ulong handle,ref MediaConfig config);
        // These imports exist only in the separately constrained live assembly.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint StartFunction(ulong handle, ref RecordingNativeAbi.Settings settings, ref TemporaryMediaCredentials.Update value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint UpdateFunction(ulong handle, ref TemporaryMediaCredentials.Update value);
    }
    public sealed class TemporaryMediaCredentials : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] public struct Update
        {
            public IntPtr Access; public uint AccessBytes;
            public IntPtr Secret; public uint SecretBytes;
            public IntPtr Token; public uint TokenBytes;
            public ulong ExpiresUnixSeconds;
        }
        public Update Value;
        private byte[][] buffers;
        private GCHandle[] pins;
        public static bool Compatible(RecordingMedia media) => media != null && Valid(media.AccessKeyId, 16, 128)
            && Valid(media.SecretAccessKey, 16, 128) && Valid(media.SessionToken, 1, 2048);
        private static bool Valid(string text, int min, int max)
        {
            if (text == null) return false;
            foreach (char c in text) if (c < 33 || c > 126) return false;
            int length = Encoding.UTF8.GetByteCount(text); return length >= min && length <= max;
        }
        public TemporaryMediaCredentials(RecordingMedia media)
        {
            if (!Compatible(media)) throw new RecordingFailure("native_credential_size");
            buffers = new byte[3][]; pins = new GCHandle[3];
            try {
                string[] fields = { media.AccessKeyId, media.SecretAccessKey, media.SessionToken };
                for (int i = 0; i < 3; i++) {
                    buffers[i] = Encoding.UTF8.GetBytes(fields[i]); pins[i] = GCHandle.Alloc(buffers[i], GCHandleType.Pinned);
                }
                Value = new Update { Access = pins[0].AddrOfPinnedObject(), AccessBytes = (uint)buffers[0].Length,
                    Secret = pins[1].AddrOfPinnedObject(), SecretBytes = (uint)buffers[1].Length,
                    Token = pins[2].AddrOfPinnedObject(), TokenBytes = (uint)buffers[2].Length,
                    ExpiresUnixSeconds = checked((ulong)media.ExpiresAtUtc.ToUnixTimeSeconds()) };
            } catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (buffers != null) for (int i = 0; i < buffers.Length; i++) {
                if (buffers[i] != null) Array.Clear(buffers[i], 0, buffers[i].Length);
                if (pins[i].IsAllocated) pins[i].Free();
            }
            Value = default; buffers = null; pins = null;
            // Immutable protocol strings are not claimed securely erased. Only
            // these owned temporary UTF8 buffers are pinned/cleared synchronously.
        }
    }
    public sealed class RecordingMediaSettings
    {
        public readonly bool External;
        public readonly uint Seconds, Width, Height, Fps;
        public readonly ulong EncodedByteLimit;
        public RecordingMediaSettings(bool external, uint seconds, uint width, uint height, ulong encodedByteLimit, RecordingRunProfile profile = null)
        { External = external; Seconds = seconds; Width = width; Height = height; Fps=(uint)(profile?.Fps??30); EncodedByteLimit = encodedByteLimit;
          if(profile!=null&&(width!=profile.Width||height!=profile.Height))throw new RecordingFailure("media_profile_mismatch"); }
    }
    public sealed class NativeRecordingRunner : IRecordingRunner
    {
        private readonly RecordingSessionClient session;
        private readonly IRecordingClock clock;
        private readonly IRecordingNativeRun native;
        private readonly string ca;
        private readonly bool external;
        private readonly RecordingMediaSettings mediaSettings;
        public RecordingNativeAbi.ExternalStatus InputStatus { get; private set; }
        // Retain positive readiness evidence and sampled SDK counts per producer
        // owner. Final Ready=false or a failed later poll must not erase milestones.
        public ulong? ObservedInputOriginQpc { get; private set; }
        public ulong? ObservedInputFrequencyHz { get; private set; }
        public ulong? SdkAcceptedVideo { get; private set; }
        public ulong? SdkAcceptedAudio { get; private set; }
        // D19 finite startup phases from ABI1 kind13 observations. Older native
        // DLLs leave these unknown; loss/release must not erase prior evidence.
        public uint? StartupPhase { get; private set; }
        public uint? StartupPhaseElapsedMs { get; private set; }
        public RecordingNativeAbi.Clock ReadClock() => ((IExternalNativeRun)native).ReadClock();
        public bool InputReady => external && started && !stopping && InputStatus.Ready == 1;
        public uint Push(ref RecordingNativeAbi.ExternalInput input) => InputReady ? ((IExternalNativeRun)native).Push(ref input) : RecordingNativeAbi.NotReady;
        private readonly Guid run = Guid.NewGuid();
        public Guid RunId => run;
        private readonly RecordingClockGroups groups = new RecordingClockGroups();
        private Task loading;
        private RecordingMedia pending;
        private RecordingTimingObservation correspondence;
        private Guid sessionId;
        private int generation;
        private string stream;
        private DateTimeOffset acceptedExpiry;
        private double began, authorizedUntil;
        private ulong droppedObservations, droppedClocks;
        private bool stopping, started, released, canRelease;
        public bool Ready { get; private set; }
        public bool Released => released;
        public string Error { get; private set; }
        public bool Degraded { get; private set; }
        // Last successfully polled state, not a guessed final/released state.
        public uint? NativeState { get; private set; }
        public NativeRecordingRunner(RecordingSessionClient session, IRecordingClock clock, IRecordingNativeRun native, string ca, RecordingMediaSettings mediaSettings)
        { this.session = session; this.clock = clock; this.native = native; this.ca = ca; this.mediaSettings = mediaSettings; this.external = mediaSettings.External; session.Authorized += Authorized; }
        public void Begin()
        { if (loading != null) return; began = clock.Seconds; loading = Task.Run(() => native.Load()); }
        private void Authorized(RecordingAuthorization authorization)
        {
            // Publication belongs to this session. Freeze its generation on the
            // first active grant; stale, late, expired or changed-stream grants
            // cannot resurrect a stopped generation or refresh another stream.
            if (stopping || released || session.State != RecordingSessionState.Active || authorization.Media == null) return;
            var media = authorization.Media;
            if (generation != 0 && (sessionId != session.SessionId || generation != session.AdmissionGeneration)) return;
            if (double.IsNaN(authorization.ReceivedMonotonicSeconds) || double.IsInfinity(authorization.ReceivedMonotonicSeconds)
                || double.IsNaN(authorization.RequestDurationSeconds) || double.IsInfinity(authorization.RequestDurationSeconds)
                || authorization.RequestDurationSeconds < 0 || authorization.RequestDurationSeconds > 10
                || authorization.ReceivedMonotonicSeconds > clock.Seconds) return;
            // Like signed-grant expiry, media eligibility subtracts the whole
            // observed request duration. Response transit cannot extend a lease.
            var now = authorization.ServerUtc.AddSeconds(clock.Seconds - authorization.ReceivedMonotonicSeconds + authorization.RequestDurationSeconds);
            if (media.ExpiresAtUtc <= now.AddSeconds(60) || media.ExpiresAtUtc <= acceptedExpiry
                || !session.Trust.TryStream(media.StreamArn, out var name) || (stream != null && stream != name)) return;
            if (!TemporaryMediaCredentials.Compatible(media)) { Error = "native_credential_size"; return; }
            sessionId = session.SessionId; generation = session.AdmissionGeneration; stream = name;
            pending = media; acceptedExpiry = media.ExpiresAtUtc;
            authorizedUntil = clock.Seconds + (media.ExpiresAtUtc - now).TotalSeconds;
        }
        public void Tick(RecordingTiming timing, bool generating)
        {
            if (released || loading == null || !loading.IsCompleted) return;
            if (loading.IsFaulted || native.Error != null) { Error = native.Error ?? "native_load_failed"; stopping = true; }
            try {
                if (!stopping && timing != null && correspondence == null) {
                    began = clock.Seconds; // Native phase starts after admission/session anchoring.
                    correspondence = native.Measure(run, timing, clock); timing.Observe(correspondence);
                }
                if (!stopping && generating && pending != null && timing != null && correspondence != null
                    && session.State == RecordingSessionState.Active && timing.SessionId == sessionId
                    && timing.AdmissionGeneration == generation && clock.Seconds < authorizedUntil - 60) {
                    uint result;
                    if (!started) {
                        if (!Path.IsPathRooted(ca) || !File.Exists(ca)) throw new RecordingFailure("native_ca_missing");
                        var settings = new RecordingNativeAbi.Settings { Size = (uint)Marshal.SizeOf<RecordingNativeAbi.Settings>(),
                            Seconds = mediaSettings.Seconds, Width = mediaSettings.Width, Height = mediaSettings.Height, Fps = mediaSettings.Fps, ObservationCapacity = 512,
                            StartupMs = 10000, FlushMs = 5000, EncodedByteLimit = mediaSettings.EncodedByteLimit,
                            SessionId = Text(sessionId.ToString("D"), 37), RunId = Text(run.ToString("D"), 37), StreamName = Text(stream, 129), CaFile = Text(ca, 1024),
                            ManagedTicks = correspondence.ManagedTicks.Value, ManagedFrequency = correspondence.ManagedFrequencyHz.Value,
                            CorrespondenceQpc = correspondence.CorrespondenceQpc.Value, CorrespondenceUncertainty100ns = correspondence.CorrespondenceUncertainty100ns.Value };
                        result = native.Start(settings, pending); if (result == 0) started = true;
                    } else result = native.Update(pending);
                    pending = null;
                    if (result != 0) { Error = "native_authorization_rejected"; Stop(); }
                }
                if (!started && !stopping && timing != null && clock.Seconds - began > 30) { Error = Error ?? "native_admission_deadline"; Stop(); }
                if (started) {
                    var status = native.Poll(); NativeState = status.State; Ready = status.State == 2 && status.AcceptedVideo > 0;
                    SdkAcceptedVideo = Math.Max(SdkAcceptedVideo ?? 0, status.AcceptedVideo);
                    SdkAcceptedAudio = Math.Max(SdkAcceptedAudio ?? 0, status.AcceptedAudio);
                    // C1's degradation flag includes ordinary frame gaps and observation
                    // loss. It is sticky evidence of incomplete coverage, not a fatal
                    // encoder/authorization error: continue external input after gaps.
                    Degraded |= status.Degraded != 0;
                    if (status.DroppedObservations != droppedObservations && timing != null) {
                        droppedObservations = status.DroppedObservations;
                        timing.Observe(new RecordingTimingObservation { RunId = run, Kind = "gap", DroppedObservations = droppedObservations });
                    }
                    // Exactly one batch per queue/Tick. No busy loop or UI wait.
                    var observations = native.Drain();
                    foreach (var row in observations) {
                        if(row.Kind==13 && row.Flags>=1 && row.Flags<=7
                            && (!StartupPhase.HasValue || row.Flags>=StartupPhase.Value)) {
                            StartupPhase=row.Flags;StartupPhaseElapsedMs=row.Result;
                        }
                        timing?.Observe(RecordingNativeAbi.ToTiming(run, row));
                    }
                    int extraCount;
                    if (external) {
                        var inputs = (IExternalNativeRun)native;
                        InputStatus = inputs.PollExternal();
                        if (InputStatus.OriginQpc > 0 && InputStatus.Frequency > 0 && !ObservedInputOriginQpc.HasValue) {
                            ObservedInputOriginQpc = InputStatus.OriginQpc; ObservedInputFrequencyHz = InputStatus.Frequency;
                        }
                        var rows = inputs.DrainExternal(); extraCount = rows.Length;
                        foreach (var row in rows) timing?.Observe(new RecordingTimingObservation { RunId = run, Kind = "external-input",
                            Track = row.Track == 1 ? "video" : "audio", Sequence = row.Sequence, Result = row.Result, Flags = row.Discontinuity,
                            CaptureQpc = row.CaptureQpc, SubmissionQpc = row.SubmissionQpc, PtsNs = row.PtsNs, DurationNs = row.DurationNs,
                            InputOriginQpc = InputStatus.OriginQpc, ObservationFrequency = InputStatus.Frequency, Bytes = row.Bytes, InputProvenance = row.Provenance });
                        if (InputStatus.ObservationDrops != droppedClocks && timing != null) {
                            droppedClocks = InputStatus.ObservationDrops;
                            timing.Observe(new RecordingTimingObservation { RunId = run, Kind = "gap", DroppedObservations = droppedClocks });
                        }
                    } else {
                        var clocks = native.DrainClocks(out ulong dropped); extraCount = clocks.Length; droppedClocks = dropped;
                        if (timing != null) {
                            groups.ObserveLoss(timing, run, dropped);
                            foreach (var row in clocks) groups.Observe(timing, run, row.ToRecord(), dropped);
                        }
                    }
                    canRelease = status.State == 4 && status.WorkerDone != 0 && status.CallbacksQuiescent != 0
                        && observations.Length < 64 && extraCount < 64;
                    if (status.State == 4) {
                        // Preserve the finite native terminal reason in safe health.
                        // C7's native_video_ended hid cancellation/media/flush failure,
                        // preventing separation of the initial stop from later teardown.
                        if (status.Reason != 1) Error = Error ?? (status.Reason switch {
                            2 => "native_cancelled", 3 => "native_initialization_failed",
                            4 => "native_startup_timeout", 5 => "native_media_failed",
                            6 => "native_byte_limit", 7 => "native_flush_timeout",
                            8 => "native_credentials_failed", 9 => "native_retirement_failed",
                            _ => "native_video_ended"
                        });
                        stopping = true;
                    }
                }
                if (stopping) {
                    native.RequestStop();
                    if (!started || canRelease) {
                        if (!external && timing != null) groups.Flush(timing, run, droppedClocks);
                        released = native.StopAndRelease();
                    }
                }
            } catch { Error = "native_video_unavailable"; Stop(); if (!started) released = native.StopAndRelease(); }
            if (released) { pending = null; session.Authorized -= Authorized; }
        }
        private bool detached;
        // The gameplay host can disappear on quit/Editor Play Mode exit while load
        // or SDK stop is pending. Only this background owner may finish that lifetime;
        // it never touches Unity, Steam, a DSP buffer or an unloaded DLL.
        public void ReleaseAfterOwnerDestroyed(Action releasedEnvironment, Action<RecordingNativeRelease> observed = null)
        {
            void Observe(RecordingNativeRelease outcome) { try { observed?.Invoke(outcome); } catch { } }
            if (released) {
                // Release may have completed between the owner's last observation
                // and handoff. Honor the same idempotent dependency-release callback
                // in this immediate path as in the background retirement path.
                try { releasedEnvironment?.Invoke(); } catch { }
                Observe(RecordingNativeRelease.Released); return;
            }
            if (detached) return;
            detached = true; Stop();
            Task.Run(async () => {
                try {
                    if (loading != null) await loading.ConfigureAwait(false);
                    for (int attempt = 0; attempt < 200; attempt++) {
                        native.RequestStop();
                        if (native.StopAndRelease()) {
                            released = true;
                            try { releasedEnvironment?.Invoke(); } catch { }
                            Observe(RecordingNativeRelease.Released); return;
                        }
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                    // Never force-unload BUSY code after the existing cleanup budget.
                    // A process may exit before this callback: pending is not release proof.
                    Observe(RecordingNativeRelease.BusyRetained);
                } catch { Observe(RecordingNativeRelease.Failed); /* Retain uncertain native ownership. */ }
            });
        }
        private static byte[] Text(string value, int capacity)
        {
            var bytes = Encoding.UTF8.GetBytes(value); if (bytes.Length >= capacity || Array.IndexOf(bytes, (byte)0) >= 0) throw new RecordingFailure("native_setting_size");
            var result = new byte[capacity]; Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length); return result;
        }
        public void Stop()
        { if (stopping) return; stopping = true; pending = null; session.Authorized -= Authorized; if (loading == null) released = true; else if (loading.IsCompleted && !loading.IsFaulted) native.RequestStop(); }
    }
}
