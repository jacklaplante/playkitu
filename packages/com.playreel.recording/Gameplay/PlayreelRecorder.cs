using System;
using System.IO;
using Playreel.Client;
using Playreel.Producer;
using UnityEngine;

namespace Playreel.Gameplay
{
    // No bootstrap or UI ownership. Adding this component does not start capture,
    // discover authentication, collect logs, open a device or contact the issuer.
    public sealed class PlayreelRecorder : MonoBehaviour
    {
        private IRecordingClock clock = new RecordingClock();
        private readonly RecordingQuitGate quit = new RecordingQuitGate();
        private RecordingController controller;
        private RecordingCapture capture;
        private RecordingMicrophone microphone;
        private RecordingLogCollector logs;
        private RecordingHealthFile health;
        private RecordingRunProfile profile;
        private string runtime;
        private bool stopRequested, destroyed, microphoneConsent;
        private double began, nextHealth;
        private RecordingRetirement.Completion retirement;
        private RecordingControllerState lastState;
        private bool lastDegraded;
        private string lastNativeError;
        private RecordingHealthMedia retainedMedia;
        private RecordingHealthSnapshot lastSnapshot;
        private RecordingMicrophoneFailure microphoneFailure;
        private float gameGain = 1, microphoneGain = 1;
        private int lastDeliveredObjects;
        public bool CanBegin => !destroyed && isActiveAndEnabled && !quit.Requested && controller == null && (retirement == null || retirement.OwnershipReleased);
        public float GameGain { get => gameGain; set { gameGain = RecordingMicrophone.ClampGain(value); if (capture != null) capture.GameGain = gameGain; } }
        public float MicrophoneGain { get => microphoneGain; set { microphoneGain = RecordingMicrophone.ClampGain(value); if (capture != null) capture.MicrophoneGain = microphoneGain; } }
        public bool QuitRequested => quit.Requested;
        public bool MediaReady => controller?.Runner?.Ready ?? false;
        public bool DiagnosticLogsUploading => controller?.Uploader?.State == RecordingUploaderState.Running;
        public int DeliveredObjects => controller?.Uploader?.UploadedObjects ?? lastDeliveredObjects;
        public ulong AcceptedAudio => (controller?.Runner as NativeRecordingRunner)?.InputStatus.AcceptedAudio ?? retainedMedia.AcceptedAudio;
        public RecordingMicrophoneState MicrophoneState => microphone?.State ?? (microphoneFailure != RecordingMicrophoneFailure.None ? RecordingMicrophoneState.Unavailable
            : microphoneConsent && !stopRequested ? RecordingMicrophoneState.Starting : RecordingMicrophoneState.Off);
        public RecordingHealthSnapshot LastHealthSnapshot => retirement?.FinalSnapshot ?? lastSnapshot;
        public string Failure { get; private set; }
        public RecordingControllerState State => controller?.State ?? retirement?.State ?? lastState;
        public bool Degraded => controller?.Runner?.Degraded ?? lastDegraded;
        public string NativeError => controller?.Runner?.Error ?? lastNativeError;
        public bool MicrophoneActive => microphone?.State == RecordingMicrophoneState.Active;

        public bool Begin(bool requiredConsent, bool optionalMicrophoneConsent, IRecordingFactory factory,
            RecordingRunProfile frozenProfile, string runtimeDirectory, string buildVersion,
            string healthKind = "playreel.recording-health")
        {
            if (!requiredConsent || !CanBegin) return false;
            // Consent is an explicit call supplied by the host; a serialized flag,
            // assembly define or package import can never silently arm a session.
            try {
                profile = frozenProfile ?? throw new ArgumentNullException(nameof(frozenProfile));
                GameGain = profile.GameGain; MicrophoneGain = profile.MicrophoneGain; lastDeliveredObjects = 0;
                runtime = Path.GetFullPath(runtimeDirectory); began = clock.Seconds;
                microphoneConsent = optionalMicrophoneConsent; stopRequested = false; Failure = null;
                retirement = null; retainedMedia = default; lastSnapshot = null; health = null;
                microphoneFailure = RecordingMicrophoneFailure.None; lastNativeError = null; lastDegraded = false;
                controller = new RecordingController(clock, factory, new RecordingBuild(buildVersion), profile.Upload);
                controller.InputStopped += StopInput;
                if (factory.IsLive) {
                    using (var process = System.Diagnostics.Process.GetCurrentProcess())
                        health = RecordingHealthFile.AfterConsent(true, Path.Combine(Application.persistentDataPath, "playreel", "Health"), process.Id, buildVersion, healthKind: healthKind);
                }
                controller.Begin(true, optionalMicrophoneConsent);
                // Never upload Editor logs. Offline lifecycle samples also avoid
                // subscriptions, health files, native code and physical devices.
                if (factory.IsLive && !Application.isEditor && controller.State == RecordingControllerState.Starting) { logs = new RecordingLogCollector(); logs.Begin(); }
                PublishHealth(); return true;
            } catch { Failure = "recording_unavailable"; Stop(); return false; }
        }
        public void Stop() { StopInput(); controller?.Stop(); }
        public void StopMicrophone()
        {
            retainedMedia = ReadMedia();
            microphoneConsent = false; microphone?.Dispose(); microphone = null;
            capture?.StopMicrophone();
        }
        private void StopInput()
        {
            // Controller-owned natural expiry and user End use the same stop intent
            // before expected native_cancelled can be classified on the next Tick.
            stopRequested = true;
            try { StopMicrophone(); capture?.StopInput(); }
            finally { logs?.StopAndDrain(controller?.Uploader); logs = null; }
        }
        private void Update()
        {
            if (controller == null || destroyed) return;
            try {
                controller.Tick(); logs?.Drain(controller.Uploader);
                if (controller.StartupError != null || controller.Session?.State == RecordingSessionState.Unavailable) { Failure = Failure ?? "session_unavailable"; StopInput(); }
                if (clock.Seconds - began > 45 && controller.Uploader == null) { Failure = Failure ?? "admission_unavailable"; Stop(); }
                if (controller.State == RecordingControllerState.Starting && Failure == null && controller.Runner?.NativeState != 2 && controller.Uploader != null && clock.Seconds - began > 80) { Failure = "initialization_timeout"; Stop(); }
                string error = controller.Runner?.Error;
                if (capture?.Error != null || (error != null && !(stopRequested && error == "native_cancelled"))) { Failure = Failure ?? capture?.Error ?? "capture_unavailable"; capture?.StopInput(); }
                var native = controller.Runner as NativeRecordingRunner;
                if (!stopRequested && Failure == null && native?.InputReady == true && capture == null) {
                    capture = gameObject.AddComponent<RecordingCapture>(); capture.Begin(native, () => controller?.Timing, profile, runtime);
                    capture.GameGain = gameGain; capture.MicrophoneGain = microphoneGain;
                }
                if (microphoneConsent && microphone == null && !stopRequested && capture != null && !capture.InputStopped && capture.Error == null && controller.State == RecordingControllerState.Running) {
                    try { microphone = new RecordingMicrophone(new UnityRecordingMicrophone(new UnityRecordingMicrophoneDevice())); capture.Microphone = microphone; microphone.SetConsent(true, AudioSettings.dspTime); }
                    catch { microphoneFailure = RecordingMicrophoneFailure.BackendException; StopMicrophone(); } // Optional device failure never changes gameplay/admission.
                }
                if (clock.Seconds >= nextHealth || controller.State == RecordingControllerState.Finished) { nextHealth = clock.Seconds + 1; PublishHealth(); }
                if (controller.State == RecordingControllerState.Finished) {
                    // Publish once with the actual last capture counters BEFORE its
                    // object disappears. No later null-capture tick can overwrite it.
                    lastState = controller.State; lastDegraded = Degraded; lastNativeError = NativeError; lastDeliveredObjects = DeliveredObjects;
                    controller.InputStopped -= StopInput; controller.Dispose(); controller = null;
                    if (capture != null) { Destroy(capture); capture = null; }
                }
            } catch { Failure = Failure ?? "recording_unavailable"; Stop(); PublishHealth(); }
            finally { if (quit.Requested && quit.CanQuit(clock.Seconds, controller)) Application.Quit(); }
        }
        private RecordingHealthMedia ReadMedia()
        {
            RecordingHealthProjection.Read(ref retainedMedia, controller?.Runner as NativeRecordingRunner, capture, microphone, microphoneFailure);
            return retainedMedia;
        }
        private void PublishHealth()
        {
            if (controller == null) return; // Keep the final receipt immutable until another explicit Begin.
            lastSnapshot = RecordingHealthSnapshot.Capture(controller, controller.Dependencies?.RuntimeSteamApp, stopRequested, destroyed,
                capture == null || capture.InputStopped, logs?.Collecting ?? false, ReadMedia(), Failure);
            health?.Publish(lastSnapshot);
        }
        private void OnEnable() { Application.wantsToQuit += WantsToQuit; }
        private void OnDisable()
        {
            Application.wantsToQuit -= WantsToQuit;
            try { Stop(); } catch { Failure = Failure ?? "recording_unavailable"; }
            if (controller == null) return;
            // Disabled MonoBehaviours no longer receive Update. Transfer this SAME
            // stopped owner to an enabled, bounded main-thread retirement pump.
            // No new admission, capture, ticket or device can start there.
            var media = ReadMedia();
            controller.InputStopped -= StopInput;
            lastDeliveredObjects = DeliveredObjects;
            retirement = RecordingRetirement.Begin(controller, health, clock, profile?.QuitGraceSeconds ?? RecordingQuitGate.DefaultGraceSeconds, media, Failure, quit.Requested, quit.Deadline);
            controller = null;
            if (capture != null) { Destroy(capture); capture = null; }
        }
        private bool WantsToQuit() { bool allowed = quit.Request(clock.Seconds, profile?.QuitGraceSeconds ?? RecordingQuitGate.DefaultGraceSeconds, controller); Stop(); return allowed; }
        private void OnDestroy()
        {
            destroyed = true;
            OnDisable(); // Idempotent: OnDisable may already have transferred ownership.
            retirement?.MarkOwnerDestroyed();
        }
    }
}
