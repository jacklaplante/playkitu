using System;
using System.Diagnostics;

namespace Playkitu.Client
{
    public interface IRecordingRunner
    {
        bool Ready { get; }
        bool Released { get; }
        string Error { get; } // Fatal failure only; recoverable native gaps are health.
        bool Degraded { get; }
        uint? NativeState { get; }
        void Begin();
        void Tick(RecordingTiming timing, bool generating);
        void Stop();
    }
    public interface IRecordingFactory
    {
        bool IsLive { get; }
        RecordingDependencies CreateAfterConsent();
    }
    public enum RuntimeSteamAppObservation { Match, Mismatch, Unavailable }
    public sealed class RecordingDependencies : IDisposable
    {
        public RecordingTrust Trust;
        public IRecordingTransport Issuer;
        public IRecordingTickets Tickets;
        public IRecordingUploadTransport Uploads;
        public Func<RecordingSessionClient, IRecordingRunner> Runner;
        public Action Pump, Release;
        public RuntimeSteamAppObservation? RuntimeSteamApp;
        public void Dispose() { Release?.Invoke(); Release = null; }
    }

    public enum RecordingControllerState { Consent, Starting, Running, Stopping, Finished }

    // One admission/native/upload lifetime serves generated proofs and gameplay.
    // The caller owns policy (deadlines, generated inputs, consent UI and quit).
    public sealed class RecordingController : IDisposable
    {
        private readonly IRecordingClock clock;
        private readonly IRecordingFactory factory;
        private readonly RecordingBuild build;
        private readonly RecordingUploadOptions options;
        private readonly IRecordingCompression compression;
        private bool closeStarted;
        public RecordingDependencies Dependencies { get; private set; }
        public IRecordingRunner Runner { get; private set; }
        public RecordingSessionClient Session { get; private set; }
        public RecordingBatchUploader Uploader { get; private set; }
        public RecordingTiming Timing { get; private set; }
        public RecordingControllerState State { get; private set; }
        public string StartupError { get; private set; }
        public event Action InputStopped;
        public RecordingController(IRecordingClock clock, IRecordingFactory factory, RecordingBuild build,
            RecordingUploadOptions options = null, IRecordingCompression compression = null)
        { this.clock = clock; this.factory = factory; this.build = build; this.options = options; this.compression = compression; }
        public bool Begin(bool consent, bool microphoneConsent = false)
        {
            if (!consent || State != RecordingControllerState.Consent) return false;
            State = RecordingControllerState.Starting;
            try {
                Dependencies = factory.CreateAfterConsent();
                Session = new RecordingSessionClient(Dependencies.Trust, Dependencies.Issuer, Dependencies.Tickets, clock, () => 0.5);
                Runner = Dependencies.Runner(Session); // Subscribe before admission publishes its first lease.
                Session.Start(true, build, microphoneConsent);
                Runner.Begin();
            } catch (Exception error) {
                // Never publish exception bodies from a credential-bearing boundary.
                StartupError = error is RecordingFailure ? (error.Message == "steam_unavailable" ? "steam_unavailable" : "recording_unavailable") : "recording_start_failed";
                Stop();
            }
            return true;
        }
        public void Tick()
        {
            if (State == RecordingControllerState.Consent || State == RecordingControllerState.Finished) return;
            Dependencies?.Pump?.Invoke(); Session?.Tick();
            // Authorization rejection/retry exhaustion calls Session.End from Tick.
            // Stop capture/log subscriptions before this tick can supply more media;
            // existing unexpired native credentials do not extend a terminal session.
            if (Session?.State == RecordingSessionState.Ending || Session?.State == RecordingSessionState.Ended
                || Session?.State == RecordingSessionState.Unavailable || Session?.CaptureDeadlineReached == true) Stop();
            if (Uploader == null && State != RecordingControllerState.Stopping && Session?.State == RecordingSessionState.Active) {
                long before = Stopwatch.GetTimestamp(); var utc = clock.UtcNow; double origin = clock.Seconds; long after = Stopwatch.GetTimestamp();
                Timing = new RecordingTiming(Session.SessionId, Session.AdmissionGeneration, build, utc, origin,
                    "managed-stopwatch", (ulong)Stopwatch.Frequency,
                    (ulong)decimal.Ceiling((decimal)(after - before) * 1000000000 / Stopwatch.Frequency), (ulong)after);
                Uploader = new RecordingBatchUploader(Session, Dependencies.Uploads, clock, Timing, options, compression);
                Uploader.Begin();
            }
            Runner?.Tick(Timing, State != RecordingControllerState.Stopping && Uploader != null);
            if (State == RecordingControllerState.Starting && Uploader != null && (Runner.Ready || Runner.Error != null)) State = RecordingControllerState.Running;
            if (State == RecordingControllerState.Stopping && (!factory.IsLive || Runner == null || Runner.Released)) Uploader?.BeginDrain();
            Uploader?.Tick();
            if (State != RecordingControllerState.Stopping) return;
            // Preserve native terminal observations before sealing the final index.
            if (!closeStarted && (Uploader == null || Uploader.State == RecordingUploaderState.Ended)) { closeStarted = true; Session?.End(); }
            if (closeStarted && (Session == null || Session.State == RecordingSessionState.Ended || Session.State == RecordingSessionState.Unavailable)
                && (Runner == null || Runner.Released)) { Dependencies?.Dispose(); State = RecordingControllerState.Finished; }
        }
        public void Stop()
        {
            if (State == RecordingControllerState.Finished || State == RecordingControllerState.Stopping) return;
            if (State == RecordingControllerState.Consent) { State = RecordingControllerState.Finished; return; }
            State = RecordingControllerState.Stopping;
            // Withdrawal before admission must cancel its ticket/HTTP enumerator
            // now. A later retirement Tick may drain an admitted session's existing
            // metadata, but must never start or finish a NEW admission after Stop.
            if (Session?.State == RecordingSessionState.Admitting) Session.End("withdrawn");
            try { InputStopped?.Invoke(); } finally { Runner?.Stop(); }
        }
        public void Dispose()
        {
            Stop(); Uploader?.Dispose(); Session?.Dispose();
            // BUSY retains DLL/environment ownership. Disposing a UI host must not
            // unload native code or restore its environment while the worker uses it.
            if (Runner == null || Runner.Released) Dependencies?.Dispose();
        }
    }
}
