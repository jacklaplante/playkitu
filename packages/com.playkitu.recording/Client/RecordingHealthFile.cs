using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Playkitu.Client
{
    public enum RecordingNativeRelease { Pending, Released, BusyRetained, Failed }
    public struct RecordingFirstPush
    {
        public bool? Attempted, Threw;
        public uint? Result;
    }
    // One per capture owner. Host retains this tiny observation object through
    // component destruction; snapshots copy values synchronously, never on workers.
    public sealed class RecordingCaptureStartup
    {
        public bool? Created, BeginCompleted;
        public RecordingFirstPush Video, Audio;
    }
    public struct RecordingHealthMedia
    {
        public bool InputReady;
        public uint? NativeStartupPhase, NativeStartupPhaseElapsedMs;
        public Guid ProducerRun;
        public ulong? ObservedInputOriginQpc, ObservedInputFrequencyHz, SdkAcceptedVideo, SdkAcceptedAudio;
        public RecordingCaptureStartup Startup;
        public RecordingMicrophoneFailure? MicrophoneFailure;
        public RecordingMicrophoneFacts? MicrophoneFacts;
        public long? MicrophoneCompletedFrames, MicrophoneMixedFrames, MicrophoneSubmittedFrames;
        public float? MicrophoneInputPeak, MicrophoneGain;
        public ulong AcceptedVideo, AcceptedAudio, DroppedVideo, DroppedAudio, InvalidInputs, Contention, ObservationDrops;
        public ulong CallerVideoDrops, CallerAudioRejected;
        public int CallerAudioDrops;
        public string AudioClockFailure;
        public double? AudioClockUncertaintyMs, AudioClockPhaseMs, AudioClockEnvelopeMs, AudioClockBracketMs;
        public double? AudioClockEstimatedPpm, AudioClockAppliedPpm, AudioClockProjectionMs;
        public long? AudioSourceConsumedFrames, AudioOutputProducedFrames;
    }

    // Immutable, explicit projection. No controller/session/native object graph,
    // upstream body, Unity log, credential, stream name or Steam identity is serialized.
    public sealed class RecordingHealthSnapshot
    {
        private readonly JObject fields;
        public readonly bool StopRequested, OwnerDestroyed, Finished, NativeReleased;
        public readonly DateTimeOffset ObservedUtc = DateTimeOffset.UtcNow;
        private RecordingHealthSnapshot(JObject fields, bool stop, bool destroyed, bool finished, bool released)
        { this.fields = fields; StopRequested = stop; OwnerDestroyed = destroyed; Finished = finished; NativeReleased = released; }
        public static RecordingHealthSnapshot Capture(RecordingController owner, RuntimeSteamAppObservation? app,
            bool stopRequested, bool ownerDestroyed, bool inputStopped, bool logsCollecting, RecordingHealthMedia media, string captureError)
        {
            var session = owner?.Session; var runner = owner?.Runner; var uploader = owner?.Uploader;
            bool stopping = stopRequested || owner?.State == RecordingControllerState.Stopping || owner?.State == RecordingControllerState.Finished;
            // C11: retain the finite clock predicate locally, independent of log
            // upload flush. Never serialize caller-supplied diagnostic text.
            string clockFailure = null;
            switch (media.AudioClockFailure) {
                case "InvalidSample": case "MeasurementUncertain": case "EpochChanged": case "ClockRegressed":
                case "PhaseEnvelope": case "RunChanged": case "NativeFrequency": case "PhaseStep": case "RateUnsupported":
                case "CorrectionSaturated": case "EstimateUnavailable": case "SourceBeforeAnchor": case "SourceInvalid":
                case "HistoryUnavailable": case "ProjectionLimit": case "TimelineStopped": clockFailure = media.AudioClockFailure; break;
            }
            var json = new JObject {
                ["consented"] = true,
                ["sessionId"] = session == null || session.SessionId == Guid.Empty ? null : session.SessionId.ToString("D"),
                ["admissionGeneration"] = session?.AdmissionGeneration,
                ["steamAppContext"] = app == null ? "unobserved" : app == RuntimeSteamAppObservation.Match ? "match" : app == RuntimeSteamAppObservation.Mismatch ? "mismatch" : "unavailable",
                ["controllerState"] = owner?.State.ToString() ?? "not_started", ["sessionState"] = session?.State.ToString() ?? "not_created",
                ["uploaderState"] = uploader?.State.ToString() ?? "not_created",
                ["ticketAttempts"] = session?.TicketAttempts ?? 0, ["admissionAttempts"] = session?.AdmissionAttempts ?? 0,
                ["lastAdmissionHttpStatus"] = session?.LastAdmissionHttpStatus,
                ["lastAdmissionResponseCode"] = RecordingDiagnostics.SafeError(session?.LastAdmissionResponseCode),
                ["steamAuthFailure"] = RecordingDiagnostics.SafeError(session?.LastSteamAuthFailure),
                ["valveErrorCode"] = session?.LastValveErrorCode,
                ["startupError"] = RecordingDiagnostics.SafeError(owner?.StartupError),
                ["sessionError"] = RecordingDiagnostics.SafeError(session?.LastError),
                ["nativeError"] = RecordingDiagnostics.SafeError(runner?.Error),
                ["uploadError"] = RecordingDiagnostics.SafeError(uploader?.LastError),
                ["captureError"] = RecordingDiagnostics.SafeError(captureError),
                ["microphoneFailure"] = media.MicrophoneFailure.HasValue && media.MicrophoneFailure != RecordingMicrophoneFailure.None
                    && Enum.IsDefined(typeof(RecordingMicrophoneFailure), media.MicrophoneFailure.Value) ? media.MicrophoneFailure.Value.ToString() : null,
                ["microphonePollGapMs"] = ClockNumber(media.MicrophoneFacts?.PollGapSeconds * 1000),
                ["microphoneUnreadFrames"] = media.MicrophoneFacts?.UnreadFrames,
                ["microphonePhaseMs"] = ClockNumber(media.MicrophoneFacts?.PhaseSeconds * 1000),
                ["microphoneReadBracketMs"] = ClockNumber(media.MicrophoneFacts?.BracketSeconds * 1000),
                ["microphoneSkippedFrames"] = media.MicrophoneFacts?.SkippedFrames,
                ["microphoneCompletedFrames"] = media.MicrophoneCompletedFrames >= 0 ? media.MicrophoneCompletedFrames : null,
                ["microphoneMixedFrames"] = media.MicrophoneMixedFrames >= 0 ? media.MicrophoneMixedFrames : null,
                ["microphoneSubmittedFrames"] = media.MicrophoneSubmittedFrames >= 0 ? media.MicrophoneSubmittedFrames : null,
                ["microphoneInputPeak"] = media.MicrophoneInputPeak >= 0 && media.MicrophoneInputPeak <= 1 ? ClockNumber(media.MicrophoneInputPeak) : JValue.CreateNull(),
                ["microphoneGain"] = media.MicrophoneGain >= 0 && media.MicrophoneGain <= 2 ? ClockNumber(media.MicrophoneGain) : JValue.CreateNull(),
                ["audioClockFailure"] = clockFailure,
                ["audioClockUncertaintyMs"] = ClockNumber(media.AudioClockUncertaintyMs),
                ["audioClockPhaseMs"] = ClockNumber(media.AudioClockPhaseMs),
                ["audioClockEnvelopeMs"] = ClockNumber(media.AudioClockEnvelopeMs),
                ["audioClockBracketMs"] = ClockNumber(media.AudioClockBracketMs),
                ["audioClockEstimatedPpm"] = BoundedClockNumber(media.AudioClockEstimatedPpm, -200, 200),
                ["audioClockAppliedPpm"] = BoundedClockNumber(media.AudioClockAppliedPpm, -200.1, 200.1),
                ["audioClockProjectionMs"] = BoundedClockNumber(media.AudioClockProjectionMs, 0, 62500),
                // Source-stage counts and resampled output frames are distinct.
                ["audioSourceConsumedFrames"] = media.AudioSourceConsumedFrames >= 0 ? media.AudioSourceConsumedFrames : null,
                ["audioOutputProducedFrames"] = media.AudioOutputProducedFrames >= 0 ? media.AudioOutputProducedFrames : null,
                ["producerRunId"] = media.ProducerRun == Guid.Empty ? null : media.ProducerRun.ToString("D"),
                ["nativeState"] = runner?.NativeState, ["inputReady"] = media.InputReady,
                ["nativeStartupPhase"] = media.NativeStartupPhase, ["nativeStartupPhaseElapsedMs"] = media.NativeStartupPhaseElapsedMs,
                // Additive D13 observations: null means unobserved/unknown, never
                // proof that a phase was not reached. Old appsrc counts stay intact.
                ["observedInputOriginQpc"] = media.ObservedInputOriginQpc,
                ["observedInputFrequencyHz"] = media.ObservedInputFrequencyHz,
                ["sdkAcceptedVideo"] = media.SdkAcceptedVideo, ["sdkAcceptedAudio"] = media.SdkAcceptedAudio,
                ["captureCreated"] = media.Startup?.Created, ["captureBeginCompleted"] = media.Startup?.BeginCompleted,
                ["firstVideoPushAttempted"] = media.Startup?.Video.Attempted,
                ["firstVideoPushResult"] = media.Startup?.Video.Result, ["firstVideoPushThrew"] = media.Startup?.Video.Threw,
                ["firstAudioPushAttempted"] = media.Startup?.Audio.Attempted,
                ["firstAudioPushResult"] = media.Startup?.Audio.Result, ["firstAudioPushThrew"] = media.Startup?.Audio.Threw,
                ["degraded"] = runner?.Degraded ?? false,
                ["acceptedVideo"] = media.AcceptedVideo, ["acceptedAudio"] = media.AcceptedAudio,
                ["droppedVideo"] = media.DroppedVideo, ["droppedAudio"] = media.DroppedAudio,
                ["invalidInputs"] = media.InvalidInputs, ["contention"] = media.Contention, ["observationDrops"] = media.ObservationDrops,
                ["callerVideoDrops"] = media.CallerVideoDrops, ["callerAudioDrops"] = media.CallerAudioDrops, ["callerAudioRejected"] = media.CallerAudioRejected,
                ["deliveredObjects"] = uploader?.UploadedObjects ?? 0,
                ["stopRequested"] = stopping, ["inputStopped"] = inputStopped, ["logsCollecting"] = logsCollecting,
                ["ownerDestroyed"] = ownerDestroyed,
                // Ended is a client state, not proof that the server accepted /close.
                ["nativeRelease"] = runner == null ? "not_created" : runner.Released ? "released" : "pending"
            };
            return new RecordingHealthSnapshot(json, stopping, ownerDestroyed, owner?.State == RecordingControllerState.Finished, runner?.Released == true);
        }
        private static JToken ClockNumber(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? new JValue(value.Value) : JValue.CreateNull();
        private static JToken BoundedClockNumber(double? value, double minimum, double maximum)
            => value >= minimum && value <= maximum ? ClockNumber(value) : JValue.CreateNull();
        internal byte[] Encode(Guid run, int pid, string build, long revision, int writeFailures, RecordingNativeRelease? release, DateTimeOffset? stopObserved, DateTimeOffset? releaseObserved, string healthKind)
        {
            var json = (JObject)fields.DeepClone();
            json["schemaVersion"] = 1; json["kind"] = healthKind;
            json["localRunId"] = run.ToString("D"); json["processId"] = pid; json["buildVersion"] = build;
            json["revision"] = revision; json["observedUtc"] = ObservedUtc.ToString("O", CultureInfo.InvariantCulture);
            json["writtenUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture); json["writeFailures"] = writeFailures;
            json["stopObservedUtc"] = stopObserved?.ToString("O", CultureInfo.InvariantCulture);
            json["nativeReleaseObservedUtc"] = releaseObserved?.ToString("O", CultureInfo.InvariantCulture);
            if (release.HasValue) json["nativeRelease"] = release == RecordingNativeRelease.Released ? "released"
                : release == RecordingNativeRelease.BusyRetained ? "busy_retained" : release == RecordingNativeRelease.Failed ? "failed" : "pending";
            return Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
        }
    }

    // Consent-only, bounded latest-state file. Publisher threads never perform disk
    // work or wait for it. One in-flight write plus one coalesced pending snapshot;
    // a worker exits when idle (no timer, spool, native polling or shutdown wait).
    public sealed class RecordingHealthFile
    {
        public const int MaximumBytes = 8192;
        private readonly object sync = new object();
        private readonly Action<string, byte[]> write;
        private readonly Guid run = Guid.NewGuid();
        private readonly int pid;
        private readonly string build, healthKind;
        private RecordingHealthSnapshot latest;
        private RecordingNativeRelease? release;
        private DateTimeOffset? stopObserved, releaseObserved;
        private bool pending, running;
        private long revision;
        private int failures;
        private double nextWrite;
        public string StatusPath { get; }
        public int PendingWrites { get { lock (sync) return pending ? 1 : 0; } }
        public bool WorkerActive { get { lock (sync) return running; } }
        public int WriteFailures { get { lock (sync) return failures; } }
        private RecordingHealthFile(string root, int pid, string build, Action<string, byte[]> write, string healthKind)
        {
            this.healthKind = healthKind; this.pid = pid; this.build = build != null && build.Length <= 64 && Array.TrueForAll(build.ToCharArray(), c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == '+') ? build : "unknown";
            StatusPath = Path.Combine(Path.GetFullPath(root), run.ToString("N"), "status.json");
            this.write = write ?? WriteAtomic;
        }
        public static RecordingHealthFile AfterConsent(bool consent, string root, int pid, string build, Action<string, byte[]> write = null, string healthKind = "playreel.recording-health")
        {
            if (!consent) return null; // No path resolution, file, worker or recurring work before consent.
            try { return new RecordingHealthFile(root, pid, build, write, healthKind); } catch { return null; }
        }
        public void Publish(RecordingHealthSnapshot snapshot)
        {
            if (snapshot == null) return;
            lock (sync) {
                // A stale main-thread snapshot cannot resurrect collection after stop,
                // destruction or completion; late native release updates only its outcome.
                if (latest != null && ((latest.StopRequested && !snapshot.StopRequested) || (latest.OwnerDestroyed && !snapshot.OwnerDestroyed) || (latest.Finished && !snapshot.Finished))) return;
                latest = snapshot;
                if (snapshot.StopRequested && !stopObserved.HasValue) stopObserved = snapshot.ObservedUtc;
                if (snapshot.NativeReleased && release != RecordingNativeRelease.Released) { release = RecordingNativeRelease.Released; releaseObserved = snapshot.ObservedUtc; }
                Queue();
            }
        }
        public void ObserveNativeRelease(RecordingNativeRelease outcome)
        {
            lock (sync) {
                if (release == RecordingNativeRelease.Released || (outcome == RecordingNativeRelease.Pending && release.HasValue)) return;
                release = outcome; releaseObserved = DateTimeOffset.UtcNow;
                if (latest != null) Queue();
            }
        }
        private void Queue()
        {
            pending = true; revision++;
            if (running) return;
            running = true;
            try { Task.Run(WriteLoop); } catch { running = false; pending = false; failures++; }
        }
        private async Task WriteLoop()
        {
            while (true) {
                double delay;
                lock (sync) { if (!pending) { running = false; return; } delay = nextWrite - Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }
                if (delay > 0) await Task.Delay((int)Math.Ceiling(Math.Min(1, delay) * 1000)).ConfigureAwait(false);
                RecordingHealthSnapshot snapshot; RecordingNativeRelease? outcome; DateTimeOffset? stoppedAt, releasedAt; long current; int lost;
                lock (sync) { snapshot = latest; outcome = release; current = revision; lost = failures; stoppedAt = stopObserved; releasedAt = releaseObserved; pending = false; }
                try {
                    var bytes = snapshot.Encode(run, pid, build, current, lost, outcome, stoppedAt, releasedAt, healthKind);
                    if (bytes.Length > MaximumBytes) throw new InvalidOperationException();
                    write(StatusPath, bytes);
                } catch { lock (sync) failures++; } // Never log arbitrary exception text or affect recording/gameplay.
                lock (sync) nextWrite = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + 1;
            }
        }
        private static void WriteAtomic(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }
}
