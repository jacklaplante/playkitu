using System;
using System.Collections;
using System.Runtime.InteropServices;
using Playreel.Client;
using Playreel.Native;
using Playreel.Producer;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Playreel.Gameplay
{
    public sealed class RecordingCapture : MonoBehaviour
    {
        public const int Width = 1280, Height = 720;
        private sealed class Slot
        {
            public RenderTexture Target;
            public NativeArray<byte> Pixels;
            public bool Pending;
            public ulong Qpc;
            public Action<AsyncGPUReadbackRequest> Completed;
        }
        private Slot[] slots = new Slot[3];
        private int width=Width,height=Height,fps=30;
        private RecordingAudioRing audioRing = new RecordingAudioRing();
        private readonly WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
        private NativeRecordingRunner runner;
        private PushExternalInput pushInput;
        private PushExternalInput observedPush;
        private RecordingCaptureStartup startup = new RecordingCaptureStartup { Created = true };
        public RecordingCaptureStartup Startup => startup;
        private RenderTexture screen, composed;
        private Texture2D cursor;
        private RecordingAudioTap tap;
        private bool stopped;
        public bool InputStopped => stopped;
        private double nextFrame, discoverAt;
        private RecordingAudioRateModel audioClock;
        private RecordingConvertedAudio convertedAudio;
        private RecordingPcmLibrary pcmLibrary;
        private string runtime;
        private Func<IRecordingPcmConverter> converterFactory; // Inject generated PCM converters in Editor tests; production uses the scoped library.
        private Func<RecordingTiming> timing;
        // Injectable DSP observation for exact production Poll/ring/submission tests;
        // the live path uses Unity's clock, never a synthetic timestamp adjustment.
        private Func<double> dspTime = () => AudioSettings.dspTime;
        private Func<double> monotonicSeconds = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        private double? audioHeadWaitBegan;
        private bool observedDspBridge;
        private RecordingTimingObservation dspBridge;
        private readonly float[] mixedAudio = new float[RecordingAudioRing.MaximumSamples];
        private GCHandle convertedPin;
        private float[] pinnedConverted;
        public RecordingMicrophone Microphone { get; set; }
        public float GameGain = 1, MicrophoneGain = 1;
        public string Error { get; private set; }
        public string AudioInputFailure { get; private set; }
        public string ClockFailure { get; private set; }
        public double? ClockUncertaintyMs { get; private set; }
        public double? ClockPhaseMs { get; private set; }
        public double? ClockEnvelopeMs { get; private set; }
        public double? ClockBracketMs { get; private set; }
        public ulong DroppedVideo { get; private set; }
        public ulong RejectedAudio { get; private set; }
        public long SubmittedMicrophoneFrames { get; private set; }
        public int DroppedAudio => audioRing.Dropped;
        public double? EstimatedAudioPpm => audioClock?.HasFit == true ? audioClock.EstimatedDspPpm : (double?)null;
        public double? AppliedAudioPpm => convertedAudio == null || convertedAudio.ConsumedFrames == 0 ? (double?)null : (convertedAudio.AppliedRatio - 1) * 1e6;
        public double? AudioProjectionMs => audioClock?.ProjectionSeconds * 1000;
        public long? ConsumedAudioFrames => convertedAudio?.ConsumedFrames;
        public long? ProducedAudioFrames => convertedAudio?.ProducedFrames;
        public void Begin(NativeRecordingRunner input, Func<RecordingTiming> timing, RecordingRunProfile profile = null, string runtime = null)
        {
            profile=profile??RecordingProfile.Standard().Freeze();
            width=profile.Width;height=profile.Height;fps=profile.Fps;
            slots=new Slot[profile.ReadbackSlots];audioRing=new RecordingAudioRing(profile.AudioRingSlots);
            runner = input; pushInput = input.Push; this.timing = timing; this.runtime = runtime;
            observedPush = ObservePush; // One cached delegate; no GPU callback allocation.
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) { Fail("graphics_api_unsupported"); return; }
            if (!SystemInfo.supportsAsyncGPUReadback) { Fail("gpu_readback_unsupported"); return; }
            if (AudioSettings.outputSampleRate != 48000 || AudioSettings.GetConfiguration().speakerMode != AudioSpeakerMode.Stereo) { Fail("audio_format_unsupported"); return; }
            AudioSettings.OnAudioConfigurationChanged += AudioChanged;
            for (int i = 0; i < slots.Length; i++) {
                var slot = new Slot { Target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB),
                    Pixels = new NativeArray<byte>(width * height * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory) };
                slot.Target.Create(); slot.Completed = request => Complete(slot, request); slots[i] = slot;
            }
            composed = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); composed.Create();
            cursor = new Texture2D(20, 28, TextureFormat.RGBA32, false);
            var pixels = new Color32[20 * 28];
            for (int y = 0; y < 26; y++) for (int x = 0; x <= Math.Min(17, y / 2); x++) {
                bool edge = x == 0 || x == y / 2 || y == 25;
                pixels[(27 - y) * 20 + x] = edge ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255);
            }
            cursor.SetPixels32(pixels); cursor.Apply();
            StartCoroutine(CaptureFrames());
            startup.BeginCompleted = true;
        }
        private void AudioChanged(bool _) { if (!stopped) Fail("audio_configuration_changed"); }
        private void Fail(string code) { Error = code; StopInput(); runner?.Stop(); }
        private void FailAudioInput(string reason, string exceptionKind = null, string detail = null)
        {
            AudioInputFailure = reason;
            // All inputs originate from literal branches or the converter's finite
            // classifier. One failure line; no arbitrary exception text or PCM.
            Debug.LogWarning("Recording audio input stopped: reason=" + reason + "; exception=" + (exceptionKind ?? "None") + "; detail=" + (detail ?? "None"));
            Fail("audio_block_invalid");
        }
        private void Update()
        {
            if (stopped) return;
            try { UpdateInput(); } catch { Fail("media_input_unavailable"); }
        }
        private void UpdateInput()
        {
            if (stopped || runner == null || !runner.InputReady) { if (tap != null) tap.SetCollecting(false); return; }
            if (Time.realtimeSinceStartupAsDouble >= discoverAt) {
                discoverAt = Time.realtimeSinceStartupAsDouble + 0.5;
                AudioListener active = null;
                foreach (var listener in FindObjectsOfType<AudioListener>()) if (listener.isActiveAndEnabled) { if (active != null) { Fail("multiple_audio_listeners"); return; } active = listener; }
                if (tap != null && (active == null || tap.gameObject != active.gameObject)) { tap.SetCollecting(false); Destroy(tap); tap = null; }
                if (active != null && tap == null) { tap = active.gameObject.AddComponent<RecordingAudioTap>(); tap.Ring = audioRing; }
            }
            if (audioRing.Invalid != 0) { FailAudioInput("RingInvalid"); return; }
            for (int attempt = 0; attempt < 2; attempt++) {
                // Reported DSP block times are related to native QPC by this main-thread
                // sample. This is an approximation, not physical speaker/capture timing.
                ulong before = (ulong)System.Diagnostics.Stopwatch.GetTimestamp();
                var sample = runner.ReadClock(); double dsp = dspTime();
                ulong after = (ulong)System.Diagnostics.Stopwatch.GetTimestamp();
                AudioSettings.GetDSPBufferSize(out int frames, out _);
                double uncertainty = frames / 48000.0 + (after - before) / (double)System.Diagnostics.Stopwatch.Frequency;
                var status = runner.InputStatus;
                bool runChanged = dspBridge != null && dspBridge.RunId != runner.RunId;
                bool frequencyChanged = sample.Frequency != status.Frequency;
                string reason = runChanged ? "RunChanged" : frequencyChanged ? "NativeFrequency" : null;
                if (reason == null && audioClock == null) {
                    if (double.IsNaN(dsp) || double.IsInfinity(dsp) || dsp < 0 || sample.Frequency == 0 || sample.Qpc < status.OriginQpc) reason = "InvalidSample";
                    else if (uncertainty + 1d / 48000 + 1d / sample.Frequency > RecordingAudioClock.MaximumUncertaintySeconds) reason = "MeasurementUncertain";
                    else {
                        // Bind frame zero only to an ACCEPTED bracket. Queued earlier
                        // callback blocks remain pre-anchor input, never restamped.
                        convertedAudio = new RecordingConvertedAudio(sample.Qpc, sample.Frequency, CreateAudioConverter, SubmitConvertedAudio, dsp);
                        audioClock = new RecordingAudioRateModel(convertedAudio);
                    }
                }
                if (reason == null && !audioClock.Observe(sample.Qpc, sample.Frequency, status.OriginQpc, dsp, uncertainty)) reason = audioClock.Failure;
                ClockBracketMs = (after - before) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                ClockUncertaintyMs = audioClock == null ? uncertainty * 1000 : audioClock.EffectiveUncertaintySeconds * 1000;
                ClockPhaseMs = audioClock?.PhaseSeconds * 1000; ClockEnvelopeMs = audioClock?.EnvelopeSeconds * 1000;
                if (reason != null) {
                    if (attempt == 0 && (reason == "MeasurementUncertain" || reason == "ProjectionLimit")) continue; // one fresh bracket, no epoch replacement.
                    ClockFailure = reason;
                    Debug.LogWarning("Recording audio clock stopped: reason=" + reason);
                    Fail("audio_clock_mismatch"); return;
                }
                if (dspBridge == null) {
                    // Retain the actual immutable epoch bracket even if the timing
                    // uploader is not available until a later Update.
                    dspBridge = new RecordingTimingObservation { RunId = runner.RunId, Kind = "clock", ObservationQpc = sample.Qpc,
                        ObservationFrequency = sample.Frequency, ManagedTicks = before, ManagedAfterTicks = after,
                        ManagedFrequencyHz = (ulong)System.Diagnostics.Stopwatch.Frequency, DspTimeSeconds = dsp,
                        CaptureUncertaintyNs = (long)Math.Ceiling(uncertainty * 1e9) };
                }
                var sink = timing();
                if (!observedDspBridge && sink != null) { sink.Observe(dspBridge); observedDspBridge = true; }
                break;
            }
            if (tap != null) tap.SetCollecting(true);
            double polledDsp = dspTime();
            Microphone?.Poll(polledDsp);
            // At13FPS,48kHz/1024-frame callbacks produce ~3.6 blocks per Update;
            // a fixed two-block drain inevitably fills the game-audio ring. Catch up
            // without chasing the DSP producer: at most the existing eight slots AND
            //4800 source frames (100ms PCM) per Update. Whole blocks keep original
            // DSP sample identities; represented conversion owns output PTS/counts.
            // No queue growth, partial source block, wait or
            // native retry; overload still drops recorder data to protect gameplay.
            int drainedFrames = 0;
            for (int budget = 0; budget < RecordingAudioRing.Capacity && audioRing.TryPeek(out var data, out int count, out double dsp); budget++) {
                if (count / 2 > RecordingAudioRing.MaximumSamples / 2 - drainedFrames) break;
                // OnAudioFilterRead publishes a render block's START, while the
                // microphone cursor only exposes COMPLETED input. Immediate drain
                // can submit that whole game interval before its mic PCM exists,
                // then discard the late voice next Update. Hold only this existing
                // recorder-owned ring slot, without changing either timestamp.
                // A first-head monotonic100ms deadline expires into honest silence,
                // even with frozen DSP/mic startup; readiness precedes conversion because
                // conversion commits source ordering and represented-rate history. Never restart a pending head's wait.
                // Off/failure/zero gain bypass the hold. Speaker/gameplay never waits.
                if (Microphone != null && Microphone.Consented
                    && (Microphone.State == RecordingMicrophoneState.Starting || Microphone.State == RecordingMicrophoneState.Active)
                    && RecordingMicrophone.ClampGain(MicrophoneGain) > 0
                    && (!Microphone.CompletedThroughDsp.HasValue || Microphone.CompletedThroughDsp.Value < dsp + count / 96000d - .5 / RecordingMicrophone.Rate)) {
                    double now = monotonicSeconds();
                    if (!audioHeadWaitBegan.HasValue) audioHeadWaitBegan = now;
                    // Invalid/regressed wall observations fall back immediately;
                    // they cannot strand the recorder-owned FIFO or block gameplay.
                    if (now >= audioHeadWaitBegan.Value && now - audioHeadWaitBegan.Value < RecordingMicrophone.CapacityFrames / (double)RecordingMicrophone.Rate) break;
                }
                audioHeadWaitBegan = null;
                if (audioRing.Invalid != 0) { audioRing.Consume(); FailAudioInput("RingInvalid"); return; }
                if (!RecordingAudioClock.TrySourceFrame(convertedAudio.DspFrameZero, dsp, count / 2, out long sourceFrame)) {
                    audioRing.Consume(); FailAudioInput("SourceIdentity"); return;
                }
                if (sourceFrame < 0) RejectedAudio++; // Explicit pre-anchor block; preserve remaining source IDs.
                else SubmitAudio(data, count / 2, dsp, sourceFrame);
                if (stopped) return;
                drainedFrames += count / 2;
                audioRing.Consume();
            }
        }
        // Listener/ring samples stay microphone-free. Native Push copies PCM before
        // returning; clear scratch even on rejection/exception, never speaker output.
        private IRecordingPcmConverter CreateAudioConverter()
        {
            if (converterFactory != null) return converterFactory();
            if (pcmLibrary == null) pcmLibrary = new RecordingPcmLibrary(runtime);
            return pcmLibrary.CreateConverter();
        }
        private void SubmitAudio(float[] data, int frames, double dsp, long sourceFrame)
        {
            try {
                long beforeMix = Microphone?.MixedFrames ?? 0;
                ulong rejected = RejectedAudio;
                long produced = convertedAudio.ProducedFrames;
                MixAudio(data, frames, dsp); // Consent/readiness/gain stay on original source DSP positions.
                if (!convertedAudio.Convert(mixedAudio, sourceFrame, frames, audioClock.RequestedRatio)) {
                    FailAudioInput(AudioInputFailure ?? convertedAudio.Failure, convertedAudio.FailureExceptionKind, convertedAudio.FailureCode); return;
                }
                if (RejectedAudio == rejected && convertedAudio.ProducedFrames > produced && Microphone != null && RecordingMicrophone.ClampGain(MicrophoneGain) > 0)
                    SubmittedMicrophoneFrames += Microphone.MixedFrames - beforeMix;
            } finally { Array.Clear(mixedAudio, 0, mixedAudio.Length); }
        }
        private bool SubmitConvertedAudio(float[] data, int sampleOffset, int frames, ulong qpc, long outputFrame)
        {
            if (!convertedPin.IsAllocated) { pinnedConverted = data; convertedPin = GCHandle.Alloc(data, GCHandleType.Pinned); }
            if (!ReferenceEquals(data, pinnedConverted)) { AudioInputFailure = "OutputBufferIdentity"; throw new RecordingFailure("audio_block_invalid"); }
            // Output is resampled media, not a raw DSP callback timestamp. Native
            // duration and PTS use the ACTUAL produced count and absolute frame index.
            var input = new RecordingNativeAbi.ExternalInput { Size = 72, Version = 1, Track = 2, Provenance = 2,
                Data = IntPtr.Add(convertedPin.AddrOfPinnedObject(), sampleOffset * sizeof(float)), Bytes = (uint)(frames * 8), Channels = 2,
                Rate = 48000, SampleFrames = (uint)frames, CaptureQpc = qpc };
            bool accepted = ObservePush(ref input) == 0;
            if (!accepted) RejectedAudio++;
            return accepted;
        }
        private uint ObservePush(ref RecordingNativeAbi.ExternalInput input)
        {
            // Both actual capture submission paths meet here. A returned zero is
            // appsrc admission only, NOT an SDK frame or persistence acknowledgement.
            ref RecordingFirstPush first = ref (input.Track == 1 ? ref startup.Video : ref startup.Audio);
            bool initial = first.Attempted != true;
            if (initial) first.Attempted = true;
            try {
                uint result = pushInput(ref input);
                if (initial) { first.Result = result; first.Threw = false; }
                return result;
            } catch {
                if (initial) first.Threw = true;
                throw;
            }
        }
        // Also exercised with generated PCM, without Begin/native/device calls.
        private void MixAudio(float[] game, int frames, double dsp)
        {
            if (Microphone != null) {
                Microphone.GameGain = GameGain; Microphone.MicrophoneGain = MicrophoneGain;
                Microphone.Mix(game, frames, dsp, mixedAudio);
            } else {
                float gain = RecordingMicrophone.ClampGain(GameGain);
                for (int i = 0; i < frames * 2; i++) mixedAudio[i] = RecordingMicrophone.ClampSample(RecordingMicrophone.ClampSample(game[i]) * gain);
            }
        }
        public void StopMicrophone()
        {
            Microphone?.Dispose(); Array.Clear(mixedAudio, 0, mixedAudio.Length);
            try { convertedAudio?.DiscardHistory(); } catch { AudioInputFailure = "ConverterCleanup"; Error = "audio_block_invalid"; }
        }
        private IEnumerator CaptureFrames()
        {
            while (!stopped) {
                yield return endOfFrame; // Final Game View includes camera and Screen Space Overlay UI.
                if (stopped || !runner.InputReady || Time.realtimeSinceStartupAsDouble < nextFrame) continue;
                nextFrame = Time.realtimeSinceStartupAsDouble + 1.0 / fps;
                try { CaptureFrame(); } catch { Fail("gpu_capture_unavailable"); }
            }
        }
        private void CaptureFrame()
        {
            Slot slot = null; foreach (var candidate in slots) if (!candidate.Pending) { slot = candidate; break; }
            if (slot == null) { DroppedVideo++; return; }
            int width = Screen.width, height = Screen.height;
            if (width < 1 || height < 1 || width > 4096 || height > 4096) { Fail("game_view_size_unsupported"); return; }
            if (screen == null || screen.width != width || screen.height != height) {
                if (screen != null) { screen.Release(); Destroy(screen); }
                screen = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); screen.Create();
            }
            slot.Qpc = runner.ReadClock().Qpc;
            RenderGameView(screen, composed, slot.Target, cursor);
            slot.Pending = true;
            try { AsyncGPUReadback.RequestIntoNativeArray(ref slot.Pixels, slot.Target, 0, TextureFormat.RGBA32, slot.Completed); }
            catch { slot.Pending = false; Fail("gpu_readback_unavailable"); }
        }
        // Shared by the production coroutine and the isolated ScreenCapture fixture.
        // ScreenCapture's D3D11 output already reads in native top-down order after
        // these Blits. A texture-only fixture has a different orientation contract.
        public static void RenderGameView(RenderTexture screen, RenderTexture composed, RenderTexture destination, Texture2D cursor, Vector2? pointerPosition = null)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) throw new InvalidOperationException("graphics_api_unsupported");
            int width = screen.width, height = screen.height;
            ScreenCapture.CaptureScreenshotIntoRenderTexture(screen);
            Graphics.Blit(screen, composed);
            // OS cursors are absent from GPU screenshots. Composite the game's
            // default arrow in the owned recording target without changing gameplay.
            if (Cursor.visible && Cursor.lockState != CursorLockMode.Locked) {
                var previous = RenderTexture.active; RenderTexture.active = composed;
                GL.PushMatrix();
                try {
                    // ScreenCapture/Blit already supplies top-down native bytes on D3D11.
                    // DrawTexture's usual top-down screen projection reflects both the
                    // cursor hotspot and its texture in this render target. Use the
                    // target's native-row projection; leave the captured world/UI alone.
                    GL.LoadPixelMatrix(0, composed.width, 0, composed.height);
                    // Explicit position lets the isolated GPU fixture share the real ScreenCapture compositor.
                    Vector2 pointer = pointerPosition ?? (Vector2)Input.mousePosition;
                    Graphics.DrawTexture(new Rect(pointer.x * composed.width / width, (height - pointer.y) * composed.height / height, 20, 28), cursor);
                } finally { GL.PopMatrix(); RenderTexture.active = previous; }
            }
            Graphics.Blit(composed, destination);
        }
        public static void CopyTopDown(RenderTexture source, RenderTexture destination)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) throw new InvalidOperationException("graphics_api_unsupported");
            // Generated Texture2D -> RT fixtures require this conversion. ScreenCapture
            // has a different source orientation; production uses RenderGameView.
            Graphics.Blit(source, destination, new Vector2(1, -1), new Vector2(0, 1));
        }
        private void Complete(Slot slot, AsyncGPUReadbackRequest request)
        {
            slot.Pending = false;
            if (stopped) { Release(slot); return; }
            if (request.hasError || !runner.InputReady || slot.Qpc < runner.InputStatus.OriginQpc) { DroppedVideo++; return; }
            // The controlled Game View fixture checks this exact path, including
            // Screen Space Overlay UI and the RGBA bytes at the native-input boundary.
            if (PushVideo(observedPush, slot.Pixels, slot.Qpc, width, height, fps) != 0) DroppedVideo++;
        }
        public delegate uint PushExternalInput(ref RecordingNativeAbi.ExternalInput input);
        public static unsafe uint PushVideo(PushExternalInput push, NativeArray<byte> pixels, ulong qpc, int width = Width, int height = Height, int fps = 30)
        {
            if(width<64||width>1280||height<64||height>720||width%2!=0||height%2!=0||fps<1||fps>30||!pixels.IsCreated||pixels.Length!=width*height*4)throw new RecordingFailure("capture_profile_mismatch");
            var input = new RecordingNativeAbi.ExternalInput { Size = 72, Version = 1, Track = 1, Provenance = 1,
                Width = (uint)width, Height = (uint)height, StrideBytes = (uint)width * 4, Bytes = (uint)(width * height * 4),
                Data = (IntPtr)pixels.GetUnsafeReadOnlyPtr(), CaptureQpc = qpc, DurationNs = 1000000000 / (uint)fps };
            return push(ref input);
        }
        private static void Release(Slot slot)
        {
            if (slot.Pixels.IsCreated) slot.Pixels.Dispose();
            if (slot.Target != null) { slot.Target.Release(); Destroy(slot.Target); slot.Target = null; }
        }
        public void StopInput()
        {
            StopMicrophone();
            if (stopped) return; stopped = true; StopAllCoroutines();
            AudioSettings.OnAudioConfigurationChanged -= AudioChanged;
            if (tap != null) { tap.SetCollecting(false); Destroy(tap); tap = null; }
            foreach (var slot in slots ?? Array.Empty<Slot>()) if (slot != null && !slot.Pending) Release(slot);
            // Ring samples stay managed and reachable by a late DSP callback; no
            // callback holds a native pointer. GPU slots release in their completion.
            try { convertedAudio?.Dispose(); pcmLibrary?.Dispose(); } catch { Error = Error ?? "audio_block_invalid"; }
            if (convertedPin.IsAllocated) convertedPin.Free(); pinnedConverted = null;
            if (screen != null) { screen.Release(); Destroy(screen); screen = null; }
            if (composed != null) { composed.Release(); Destroy(composed); composed = null; }
            if (cursor != null) { Destroy(cursor); cursor = null; }
        }
        private void OnDestroy() => StopInput();
    }
}
