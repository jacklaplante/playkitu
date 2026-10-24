using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Playkitu.Client;
using Playkitu.Gameplay;
using Playkitu.Native;
using Playkitu.Producer;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Playkitu.Tests
{
    public sealed class PlaykituLifecycleTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Fields).SetValue(target, value);
        private static object Get(object target, string field) => target.GetType().GetField(field, Fields).GetValue(target);
        private static void Property(object target, string property, object value) => target.GetType().GetProperty(property).SetValue(target, value);
        private static void Tick(object target) => target.GetType().GetMethod("Update", Fields).Invoke(target, null);
        private static JObject Snapshot(RecordingHealthSnapshot snapshot) => (JObject)Get(snapshot, "fields");
        private static MonoBehaviour Retirement() => Resources.FindObjectsOfTypeAll<MonoBehaviour>().Single(x => x.GetType().Name == "RecordingRetirement");
        private static bool OwnershipReleased(PlaykituRecorder host) => (bool)Get(host, "retirement").GetType().GetProperty("OwnershipReleased", Fields).GetValue(Get(host, "retirement"));

        [TearDown] public void CleanupOnlySampleRetirementOwners()
        {
            foreach (var owner in Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(x => x.GetType().Name == "RecordingRetirement").ToArray()) Object.DestroyImmediate(owner.gameObject);
        }
        private static PlaykituRecorder Start(GameObject go, Factory factory)
        {
            var host = go.AddComponent<PlaykituRecorder>(); Set(host, "clock", factory.Clock);
            Assert.That(host.Begin(true, false, factory, RecordingProfile.Standard().Freeze(), ".", "offline-lifecycle"), Is.True);
            for (int i = 0; i < 20 && host.State != RecordingControllerState.Running; i++) { factory.Clock.Seconds += .01; Tick(host); }
            Assert.That(host.State, Is.EqualTo(RecordingControllerState.Running));
            return host;
        }
        [TestCase(false), TestCase(true)]
        public void DisabledActiveOrDrainingHostTransfersSameControllerAndFinishes(bool alreadyDraining)
        {
            var factory = new Factory(); var go = new GameObject("offline disabled recorder");
            try {
                var host = Start(go, factory); var controller = (RecordingController)Get(host, "controller");
                Set(host, "retainedMedia", new RecordingHealthMedia { CallerAudioDrops = 7, AudioOutputProducedFrames = 123456 });
                if (alreadyDraining) { host.Stop(); Tick(host); Assert.That(host.State, Is.EqualTo(RecordingControllerState.Stopping)); }
                go.SetActive(false);
                Assert.That(Get(host, "controller"), Is.Null);
                var owner = Retirement(); var completion = Get(owner, "completion");
                Assert.That(Get(completion, "controller"), Is.SameAs(controller));
                Assert.That(host.Begin(true, false, factory, RecordingProfile.Standard().Freeze(), ".", "blocked"), Is.False);
                factory.Runner.Hold = false;
                for (int i = 0; i < 100 && owner != null; i++) { factory.Clock.Seconds += .1; Tick(owner); Thread.Sleep(1); }
                Assert.That(host.State, Is.EqualTo(RecordingControllerState.Finished));
                Assert.That(factory.Releases, Is.EqualTo(1)); Assert.That(factory.Closes, Is.EqualTo(1));
                Assert.That(factory.Runner.Stops, Is.EqualTo(1)); Assert.That(OwnershipReleased(host), Is.True);
                Assert.That(Snapshot(host.LastHealthSnapshot).Value<long>("audioOutputProducedFrames"), Is.EqualTo(123456));
                Assert.That(Snapshot(host.LastHealthSnapshot).Value<int>("callerAudioDrops"), Is.EqualTo(7));
            } finally { Object.DestroyImmediate(go); }
        }
        [Test] public void FinishedSnapshotIsPublishedOnceAndExplicitRestartUsesFreshSession()
        {
            var factory = new Factory(); var go = new GameObject("offline repeated finish");
            try {
                var host = Start(go, factory); var firstController = (RecordingController)Get(host, "controller");
                Set(host, "retainedMedia", new RecordingHealthMedia { AudioSourceConsumedFrames = 48000, AudioOutputProducedFrames = 47967, CallerAudioDrops = 3 });
                factory.Runner.Hold = false; host.Stop();
                for (int i = 0; i < 100 && host.State != RecordingControllerState.Finished; i++) { factory.Clock.Seconds += .1; Tick(host); Thread.Sleep(1); }
                Assert.That(host.State, Is.EqualTo(RecordingControllerState.Finished));
                var final = host.LastHealthSnapshot; string finalJson = Snapshot(final).ToString();
                for (int i = 0; i < 20; i++) Tick(host);
                Assert.That(host.LastHealthSnapshot, Is.SameAs(final)); Assert.That(Snapshot(final).ToString(), Is.EqualTo(finalJson));
                Assert.That(factory.Releases, Is.EqualTo(1)); Assert.That(Get(host, "controller"), Is.Null);
                var second = new Factory { Clock = factory.Clock };
                Assert.That(host.Begin(true, false, second, RecordingProfile.Standard().Freeze(), ".", "second"), Is.True);
                Assert.That(((RecordingController)Get(host, "controller")).Session.SessionId, Is.Not.EqualTo(firstController.Session.SessionId));
                Assert.That(host.LastHealthSnapshot, Is.Not.SameAs(final)); Assert.That(Snapshot(final).ToString(), Is.EqualTo(finalJson));
                second.Runner.Hold = false; host.Stop();
                for (int i = 0; i < 30 && host.State != RecordingControllerState.Finished; i++) { second.Clock.Seconds += .1; Tick(host); Thread.Sleep(1); }
            } finally { Object.DestroyImmediate(go); }
        }
        [Test] public void RetirementDeadlineDisposesManagedWorkButBlocksRestartUntilNativeRelease()
        {
            var fake = new Native(); var factory = new Factory { Native = fake }; var go = new GameObject("offline busy retirement");
            try {
                var host = go.AddComponent<PlaykituRecorder>(); Set(host, "clock", factory.Clock);
                Assert.That(host.Begin(true, false, factory, RecordingProfile.Standard().Freeze(), ".", "busy"), Is.True);
                Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref fake.Loads) == 1, 2000), Is.True);
                go.SetActive(false); var owner = Retirement();
                int quitCalls = 0; Set(owner, "quitApplication", (Action)(() => quitCalls++));
                Assert.That(owner.GetType().GetMethod("WantsToQuit", Fields).Invoke(owner, null), Is.False);
                factory.Clock.Seconds += RecordingQuitGate.MaximumGraceSeconds + 1; Tick(owner);
                // Play Mode Destroy is deferred until frame end; work must already
                // be complete at this deadline, independently of object removal.
                Assert.That((bool)Get(Get(owner, "completion"), "complete"), Is.True);
                Assert.That(factory.Releases, Is.Zero); Assert.That(OwnershipReleased(host), Is.False);
                Assert.That(quitCalls, Is.EqualTo(1), "Quit must use the original bounded retirement deadline.");
                Assert.That(factory.TicketCalls, Is.Zero, "Retirement cannot admit/request a ticket after withdrawal.");
                go.SetActive(true);
                Assert.That(host.Begin(true, false, new Factory(), RecordingProfile.Standard().Freeze(), ".", "too-early"), Is.False);
                Volatile.Write(ref fake.AllowRelease, 1);
                Assert.That(SpinWait.SpinUntil(() => OwnershipReleased(host), 2000), Is.True);
                Assert.That(factory.Releases, Is.EqualTo(1));
                var next = new Factory { Clock = factory.Clock };
                Assert.That(host.Begin(true, false, next, RecordingProfile.Standard().Freeze(), ".", "after-release"), Is.True);
                next.Runner.Hold = false; host.Stop(); for (int i = 0; i < 30 && host.State != RecordingControllerState.Finished; i++) { next.Clock.Seconds += .1; Tick(host); }
            } finally { Volatile.Write(ref fake.AllowRelease, 1); Object.DestroyImmediate(go); }
        }
        [Test] public void AlreadyReleasedNativeHandoffStillReleasesDependencyCallback()
        {
            var factory = new Factory { Native = new Native() }; var clock = factory.Clock;
            using (var controller = new RecordingController(clock, factory, new RecordingBuild("offline"))) {
                controller.Begin(true); var runner = (NativeRecordingRunner)controller.Runner;
                Set(runner, "released", true); int released = 0;
                runner.ReleaseAfterOwnerDestroyed(() => released++);
                Assert.That(released, Is.EqualTo(1));
            }
        }
        [TestCase(false, false), TestCase(true, false), TestCase(true, true)]
        public void QuitBeforeDisableOrDestroyKeepsIntentAndOriginalAbsoluteDeadline(bool timeout, bool destroyHost)
        {
            var factory = new Factory(); var go = new GameObject("offline quit then handoff");
            try {
                var host = Start(go, factory);
                Assert.That(typeof(PlaykituRecorder).GetMethod("WantsToQuit", Fields).Invoke(host, null), Is.False);
                double deadline = ((RecordingQuitGate)Get(host, "quit")).Deadline;
                factory.Clock.Seconds += 2;
                if (destroyHost) Object.DestroyImmediate(go); else go.SetActive(false);
                var owner = Retirement(); var completion = Get(owner, "completion");
                Assert.That(Get(completion, "deadline"), Is.EqualTo(deadline)); Assert.That(Get(owner, "quitRequested"), Is.True);
                int quitCalls = 0; Set(owner, "quitApplication", (Action)(() => quitCalls++));
                factory.Runner.Hold = timeout;
                for (int i = 0; i < 100 && quitCalls == 0; i++) { factory.Clock.Seconds = Math.Min(deadline, factory.Clock.Seconds + .1); Tick(owner); Thread.Sleep(1); }
                Assert.That(quitCalls, Is.EqualTo(1)); Assert.That(factory.Clock.Seconds, Is.LessThanOrEqualTo(deadline));
                if (!timeout) Assert.That(factory.Releases, Is.EqualTo(1));
            } finally { if (go != null) Object.DestroyImmediate(go); }
        }
        [Test] public void TypedStatusAndLiveGainsUseTheExistingCaptureOwner()
        {
            var factory = new Factory(); var go = new GameObject("offline live gains");
            try {
                var host = Start(go, factory); var capture = go.AddComponent<RecordingCapture>(); capture.enabled = false; Set(host, "capture", capture);
                host.GameGain = 1.5f; host.MicrophoneGain = .25f;
                Assert.That(capture.GameGain, Is.EqualTo(1.5f)); Assert.That(capture.MicrophoneGain, Is.EqualTo(.25f));
                host.GameGain = float.NaN; host.MicrophoneGain = 3;
                Assert.That(capture.GameGain, Is.EqualTo(1)); Assert.That(capture.MicrophoneGain, Is.EqualTo(2));
                Assert.That(host.MediaReady, Is.True); Assert.That(host.CanBegin, Is.False); Assert.That(host.QuitRequested, Is.False);
                factory.Runner.Hold = false; host.Stop(); for (int i = 0; i < 100 && !host.CanBegin; i++) { factory.Clock.Seconds += .1; Tick(host); Thread.Sleep(1); }
                Assert.That(host.CanBegin, Is.True); Assert.That(host.State, Is.EqualTo(RecordingControllerState.Finished));
            } finally { Object.DestroyImmediate(go); }
        }
        [Test] public void PublishedHealthKeepsNativeStartupSdkAndCaptureClockObservations()
        {
            var factory = new Factory { Native = new Native() }; var go = new GameObject("offline complete health");
            try {
                var host = go.AddComponent<PlaykituRecorder>(); Set(host, "clock", factory.Clock);
                Assert.That(host.Begin(true, false, factory, RecordingProfile.Standard().Freeze(), ".", "health"), Is.True);
                var controller = (RecordingController)Get(host, "controller"); var runner = (NativeRecordingRunner)controller.Runner;
                Property(runner, "SdkAcceptedVideo", (ulong?)11); Property(runner, "SdkAcceptedAudio", (ulong?)22);
                Property(runner, "ObservedInputOriginQpc", (ulong?)333); Property(runner, "ObservedInputFrequencyHz", (ulong?)1000000);
                Property(runner, "StartupPhase", (uint?)7); Property(runner, "StartupPhaseElapsedMs", (uint?)42);
                Property(runner, "InputStatus", new RecordingNativeAbi.ExternalStatus { AcceptedVideo = 12, AcceptedAudio = 23, DroppedAudio = 2, DroppedVideo = 3, InvalidInputs = 4, ProcessContentionReturns = 5, ObservationDrops = 6 });
                var capture = go.AddComponent<RecordingCapture>(); capture.enabled = false;
                capture.Startup.BeginCompleted = true; capture.Startup.Video = new RecordingFirstPush { Attempted = true, Result = 0, Threw = false };
                capture.Startup.Audio = new RecordingFirstPush { Attempted = true, Result = 0, Threw = false };
                Property(capture, "ClockUncertaintyMs", (double?)21.4); Property(capture, "ClockPhaseMs", (double?)-2);
                Property(capture, "ClockEnvelopeMs", (double?)23.4); Property(capture, "ClockBracketMs", (double?).1);
                var converted = new RecordingConvertedAudio(1, 1000000, () => null, (_, __, ___, ____, _____) => true);
                Property(converted, "ConsumedFrames", 48000L); Property(converted, "ProducedFrames", 47967L); Set(converted, "previousRatio", 1.00002d);
                Set(capture, "convertedAudio", converted);
                var rate = new RecordingAudioRateModel(converted); Property(rate, "HasFit", true); Property(rate, "EstimatedDspPpm", 12d); Property(rate, "ProjectionSeconds", .1d); Set(capture, "audioClock", rate);
                Set(host, "capture", capture);
                var microphone = new RecordingMicrophone(new MicrophoneFacts());
                Property(microphone, "CompletedFrames", 1200L); Property(microphone, "MixedFrames", 1000L); Property(microphone, "InputPeak", .5f);
                Property(capture, "SubmittedMicrophoneFrames", 900L); capture.MicrophoneGain = .75f; Set(host, "microphone", microphone);
                typeof(PlaykituRecorder).GetMethod("PublishHealth", Fields).Invoke(host, null);
                var json = Snapshot(host.LastHealthSnapshot);
                Assert.That(json.Value<ulong>("sdkAcceptedVideo"), Is.EqualTo(11)); Assert.That(json.Value<ulong>("sdkAcceptedAudio"), Is.EqualTo(22));
                Assert.That(json.Value<ulong>("observedInputOriginQpc"), Is.EqualTo(333)); Assert.That(json.Value<ulong>("observedInputFrequencyHz"), Is.EqualTo(1000000));
                Assert.That(json.Value<uint>("nativeStartupPhase"), Is.EqualTo(7)); Assert.That(json.Value<uint>("nativeStartupPhaseElapsedMs"), Is.EqualTo(42));
                Assert.That(json.Value<bool>("captureBeginCompleted"), Is.True); Assert.That(json.Value<bool>("firstAudioPushAttempted"), Is.True);
                Assert.That(json.Value<ulong>("acceptedAudio"), Is.EqualTo(23)); Assert.That(json.Value<ulong>("observationDrops"), Is.EqualTo(6));
                Assert.That(json.Value<double>("audioClockEstimatedPpm"), Is.EqualTo(12)); Assert.That(json.Value<double>("audioClockAppliedPpm"), Is.EqualTo(20).Within(.001));
                Assert.That(json.Value<double>("audioClockProjectionMs"), Is.EqualTo(100)); Assert.That(json.Value<double>("audioClockEnvelopeMs"), Is.EqualTo(23.4));
                Assert.That(json.Value<long>("audioSourceConsumedFrames"), Is.EqualTo(48000)); Assert.That(json.Value<long>("audioOutputProducedFrames"), Is.EqualTo(47967));
                Assert.That(json.Value<long>("microphoneCompletedFrames"), Is.EqualTo(1200)); Assert.That(json.Value<long>("microphoneMixedFrames"), Is.EqualTo(1000));
                Assert.That(json.Value<long>("microphoneSubmittedFrames"), Is.EqualTo(900)); Assert.That(json.Value<double>("microphoneInputPeak"), Is.EqualTo(.5));
                Assert.That(json.Value<double>("microphoneGain"), Is.EqualTo(.75)); Assert.That(json.Value<double>("microphonePollGapMs"), Is.EqualTo(10));
                Assert.That(json.Value<long>("microphoneSkippedFrames"), Is.EqualTo(2));
                Set(runner, "released", true);
                host.Stop();
            } finally { Object.DestroyImmediate(go); }
        }
        private sealed class Clock : IRecordingClock
        {
            public double Seconds { get; set; } public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddSeconds(Seconds);
        }
        private sealed class Factory : IRecordingFactory, IRecordingTransport, IRecordingTickets, IRecordingUploadTransport
        {
            public Clock Clock = new Clock(); public readonly Runner Runner = new Runner(); public Native Native;
            public int Releases, Closes, TicketCalls; private Guid session; private DateTimeOffset end;
            public bool IsLive => false; // No health file, Editor log subscription or native library/device is allowed.
            public RecordingDependencies CreateAfterConsent() => new RecordingDependencies {
                Trust = new RecordingTrust(480, "012345678901", "us-east-1", "https://example.execute-api.us-east-1.amazonaws.com/", "playkitu-", "example-playkitu-uploads"), Issuer = this, Tickets = this, Uploads = this,
                Runner = s => Native == null ? (IRecordingRunner)Runner : new NativeRecordingRunner(s, Clock, Native, "unused", new RecordingMediaSettings(true, 14400, 1280, 720, 1024)),
                Release = () => Interlocked.Increment(ref Releases) };
            public IRecordingHttpOperation Begin(RecordingHttpRequest request)
            {
                request.Validate(); var json = new JObject { ["protocolVersion"] = 1, ["serverUtc"] = Clock.UtcNow.ToString("O") }; int status = 200;
                if (request.Path == "/v1/config") {
                    json["enabled"] = true; json["appId"] = 480; json["consentSchemaVersion"] = 1; json["identityPrefix"] = RecordingIdentity.Prefix;
                    json["admissionUnixSeconds"] = Clock.UtcNow.ToUnixTimeSeconds();
                    json["limits"] = new JObject { ["heartbeatSeconds"] = 60, ["sessionSeconds"] = 14400, ["admissionDeadlineSeconds"] = 90, ["maxAdmissionGenerations"] = 3, ["maxObjectsPerRequest"] = 16 };
                } else if (request.Path == "/v1/sessions") {
                    var body = JObject.Parse(Encoding.UTF8.GetString(request.Body)); session = Guid.Parse((string)body["sessionId"]); end = Clock.UtcNow.AddHours(4);
                    json["sessionId"] = session.ToString("D"); json["generation"] = 1; json["state"] = "active"; json["videoAvailability"] = "unavailable"; json["endsAtUtc"] = end.ToString("O"); status = 201;
                } else if (request.Path.EndsWith("/close")) { Closes++; json["sessionId"] = session.ToString("D"); json["state"] = "closed"; }
                else { json["sessionId"] = session.ToString("D"); json["endsAtUtc"] = end.ToString("O"); json["mediaStatus"] = "unavailable"; json["logStatus"] = "unavailable"; json["media"] = null; json["uploads"] = new JArray(); json["remainingBytes"] = RecordingProtocol.MaxBytes; json["remainingObjects"] = RecordingDiagnosticsLimits.ObjectsPerSession; }
                return new Operation(new RecordingHttpResponse(status, Encoding.UTF8.GetBytes(json.ToString())));
            }
            public IEnumerator Begin(string identity, Func<string, IEnumerator> consumer, Action<string> failure, CancellationToken cancellation)
            { TicketCalls++; var flow = consumer("0102"); while (flow.MoveNext()) yield return flow.Current; }
            public IRecordingPreparedUpload Prepare(RecordingGrant grant, RecordingPayload payload) => throw new Exception("Offline grants cannot upload");
        }
        private sealed class Operation : IRecordingHttpOperation { private readonly RecordingHttpResponse response; public Operation(RecordingHttpResponse response) { this.response = response; } public bool IsDone => true; public RecordingHttpResponse Response => response; public void Dispose() { } }
        private sealed class Runner : IRecordingRunner
        {
            public bool Hold = true; private bool stopping; public int Stops;
            public bool Ready => true; public bool Released { get; private set; } public string Error => stopping ? "native_cancelled" : null;
            public bool Degraded => false; public uint? NativeState => Released ? 4u : stopping ? 3u : 2u;
            public void Begin() { } public void Tick(RecordingTiming timing, bool generating) { if (stopping && !Hold) Released = true; }
            public void Stop() { if (!stopping) { stopping = true; Stops++; } }
        }
        private sealed class Native : IRecordingNativeRun, IExternalNativeRun
        {
            public int Loads, AllowRelease; public string Error => null;
            public void Load() { Interlocked.Increment(ref Loads); }
            public RecordingTimingObservation Measure(Guid run, RecordingTiming timing, IRecordingClock clock) => null;
            public uint Start(RecordingNativeAbi.Settings settings, RecordingMedia media) => throw new Exception("No native/media start");
            public uint Update(RecordingMedia media) => throw new Exception("No credentials");
            public RecordingNativeAbi.Status Poll() => default;
            public RecordingNativeAbi.Observation[] Drain() => Array.Empty<RecordingNativeAbi.Observation>();
            public RecordingNativeAbi.FrameClock[] DrainClocks(out ulong dropped) { dropped = 0; return Array.Empty<RecordingNativeAbi.FrameClock>(); }
            public void RequestStop() { } public bool StopAndRelease() => Volatile.Read(ref AllowRelease) != 0;
            public RecordingNativeAbi.Clock ReadClock() => default; public RecordingNativeAbi.ExternalStatus PollExternal() => default;
            public uint Push(ref RecordingNativeAbi.ExternalInput input) => throw new Exception("No media input");
            public RecordingNativeAbi.ExternalObservation[] DrainExternal() => Array.Empty<RecordingNativeAbi.ExternalObservation>();
        }
        private sealed class MicrophoneFacts : IRecordingMicrophoneSource, IRecordingMicrophoneDiagnostics
        {
            public RecordingMicrophoneFailure Failure => RecordingMicrophoneFailure.None;
            public RecordingMicrophoneFacts Facts => new RecordingMicrophoneFacts { PollGapSeconds = .01, PhaseSeconds = .002, BracketSeconds = .001, UnreadFrames = 3, SkippedFrames = 2 };
            public bool TryOpen(out int channels, out int rate) => throw new Exception("No microphone device may open");
            public int Read(float[] destination, int maximumFrames, out double firstDsp) => throw new Exception("No microphone device may read");
            public void Close() { }
        }
    }
}
