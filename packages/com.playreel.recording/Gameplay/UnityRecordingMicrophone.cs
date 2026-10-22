using System;
using Playreel.Client;
using UnityEngine;

namespace Playreel.Gameplay
{
    // Every physical API is injectable. Construction performs no device work.
    public interface IRecordingMicrophoneDevice
    {
        bool Start(out int channels, out int rate, out int capacityFrames);
        bool IsRecording { get; }
        int Position { get; }
        double DspTime { get; }
        bool Read(float[] samples, int offsetFrames);
        void End();
    }
    // Low-level seam verifies the actual Unity default-device key without opening
    // hardware. No enumeration is needed: Unity documents null as its default key.
    public interface IUnityRecordingMicrophoneBackend
    {
        bool IsRecording(string key);
        AudioClip Start(string key, bool loop, int seconds, int rate);
        int GetPosition(string key);
        void End(string key);
    }
    internal sealed class UnityRecordingMicrophoneBackend : IUnityRecordingMicrophoneBackend
    {
        public bool IsRecording(string key) => Microphone.IsRecording(key);
        public AudioClip Start(string key, bool loop, int seconds, int rate) => Microphone.Start(key, loop, seconds, rate);
        public int GetPosition(string key) => Microphone.GetPosition(key);
        public void End(string key) => Microphone.End(key);
    }
    public sealed class UnityRecordingMicrophoneDevice : IRecordingMicrophoneDevice
    {
        private readonly IUnityRecordingMicrophoneBackend backend;
        private const string DeviceKey = null;
        private AudioClip clip;
        private float[] silence;
        private bool started;
        public UnityRecordingMicrophoneDevice(IUnityRecordingMicrophoneBackend backend = null)
        { this.backend = backend ?? new UnityRecordingMicrophoneBackend(); }
        public bool Start(out int channels, out int rate, out int capacityFrames)
        {
            channels = rate = capacityFrames = 0;
            // Use the SAME documented default key for start/poll/end; array order
            // says nothing about the OS default. Own only the returned clip and
            // refuse a default already recording before this acquisition.
            // Unity exposes no capture handle/device identity for the null key:
            // seamless OS-default changes or concurrent microphone owners cannot
            // be guaranteed. Detected loss/reset fails closed, with no auto-reopen.
            if (started || backend.IsRecording(DeviceKey)) return false;
            started = true;
            clip = backend.Start(DeviceKey, true, 1, RecordingMicrophone.Rate);
            if (clip == null) return false;
            channels = clip.channels; rate = clip.frequency; capacityFrames = clip.samples;
            silence = new float[checked(channels * capacityFrames)];
            return true;
        }
        public bool IsRecording => started && backend.IsRecording(DeviceKey);
        public int Position => started ? backend.GetPosition(DeviceKey) : 0;
        public double DspTime => AudioSettings.dspTime;
        public bool Read(float[] samples, int offsetFrames) => clip != null && clip.GetData(samples, offsetFrames);
        public void End()
        {
            try { if (started) backend.End(DeviceKey); }
            finally {
                started = false;
                try { if (clip != null && silence != null) clip.SetData(silence, 0); }
                finally {
                    // Stop acquisition first, overwrite the owned looping PCM, then
                    // release it. Runtime Destroy is deferred; PCM erasure is not.
                    if (clip != null) {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(clip);
                        else UnityEngine.Object.DestroyImmediate(clip);
                    }
                    clip = null; silence = null;
                }
            }
        }
    }
    // Maps device frame identity into the EXISTING DSP epoch once. Cursor polling
    // estimates availability time, NOT physical acoustic capture time/hardware latency.
    // No re-anchor, resampler, AudioSource, device retry or blocking startup wait.
    public sealed class UnityRecordingMicrophone : IRecordingMicrophoneSource, IRecordingMicrophoneDiagnostics
    {
        private readonly IRecordingMicrophoneDevice device;
        private bool opened, anchored, recordingObserved;
        private int capacity, previousPosition;
        private long written, read, anchorFrame;
        private double previousDsp, anchorDsp;
        private float[] block;
        private RecordingMicrophoneFacts facts;
        public RecordingMicrophoneFailure Failure { get; private set; }
        public RecordingMicrophoneFacts Facts => facts;
        public UnityRecordingMicrophone(IRecordingMicrophoneDevice device) { this.device = device; }
        public bool TryOpen(out int channels, out int rate)
        {
            opened = true;
            Failure = RecordingMicrophoneFailure.None; facts = default;
            if (!device.Start(out channels, out rate, out capacity)) { Reject(RecordingMicrophoneFailure.OpenFailed); return false; }
            if ((channels != 1 && channels != 2) || rate != RecordingMicrophone.Rate || capacity < RecordingMicrophone.CapacityFrames * 2) { Reject(RecordingMicrophoneFailure.FormatUnsupported); return false; }
            block = new float[RecordingMicrophone.ReadFrames * channels];
            return true;
        }
        public int Read(float[] destination, int maximumFrames, out double firstDsp)
        {
            firstDsp = 0;
            if (Failure != RecordingMicrophoneFailure.None) return -1;
            facts.PollGapSeconds = facts.PhaseSeconds = facts.BracketSeconds = null; facts.UnreadFrames = null;
            if (!opened || block == null || maximumFrames < RecordingMicrophone.ReadFrames) return Reject(RecordingMicrophoneFailure.SourceRejected);
            // A valid clip can precede its first recording-ready observation. Keep
            // that pending state within the coordinator's EXISTING2s startup budget,
            // without re-opening. Once recording has been seen, false is actual loss.
            if (!device.IsRecording) return recordingObserved ? Reject(RecordingMicrophoneFailure.DeviceLost) : 0;
            recordingObserved = true;
            double before = device.DspTime; int position = device.Position; double after = device.DspTime;
            if (!double.IsNaN(before) && !double.IsInfinity(before) && !double.IsNaN(after) && !double.IsInfinity(after)) facts.BracketSeconds = after - before;
            if (double.IsNaN(before) || double.IsInfinity(before) || double.IsNaN(after) || double.IsInfinity(after)
                || before < 0 || after < before || after - before > .025 || position < 0 || position >= capacity) return Reject(RecordingMicrophoneFailure.InvalidSample);
            double dsp = (before + after) * .5;
            if (!anchored) {
                if (position == 0) return 0;
                anchored = true; previousPosition = position; written = read = anchorFrame = position;
                previousDsp = anchorDsp = dsp;
                return 0; // Discard startup samples; their DSP interval is unknown.
            }
            double elapsed = dsp - previousDsp;
            facts.PollGapSeconds = elapsed;
            if (elapsed < 0) return Reject(RecordingMicrophoneFailure.ClockRegressed);
            if (elapsed >= capacity / (double)RecordingMicrophone.Rate - .1) return Reject(RecordingMicrophoneFailure.AmbiguousWrap);
            int delta = (position - previousPosition + capacity) % capacity;
            written += delta; previousPosition = position; previousDsp = dsp;
            facts.UnreadFrames = written - read;
            facts.PhaseSeconds = (written - anchorFrame) / (double)RecordingMicrophone.Rate - (dsp - anchorDsp);
            // Validate timing BEFORE discarding backlog: a cursor reset must not be
            // disguised as a harmless skip. Bounds/anchor remain unchanged.
            if (Math.Abs(facts.PhaseSeconds.Value) + (after - before) * .5 > .05) return Reject(RecordingMicrophoneFailure.PhaseEnvelope);
            if (written - read > RecordingMicrophone.CapacityFrames) {
                long skipped = written - read - RecordingMicrophone.CapacityFrames;
                read += skipped; facts.SkippedFrames += skipped;
                // Only unread stale samples are skipped. Preserve absolute frame
                // identity in firstDsp below, so delayed game blocks hear silence.
            }
            int frames = (int)Math.Min(written - read, RecordingMicrophone.ReadFrames);
            if (frames == 0) return 0;
            if (!device.Read(block, (int)(read % capacity))) return Reject(RecordingMicrophoneFailure.ReadFailed);
            firstDsp = anchorDsp + (read - anchorFrame) / (double)RecordingMicrophone.Rate;
            // GetData fills a fixed array, possibly beyond the completed cursor.
            // Publish ONLY completed frames, erase the future/old-loop tail. Partial
            // reads avoid permanent mic lag behind short game DSP blocks.
            Array.Copy(block, destination, frames * block.Length / RecordingMicrophone.ReadFrames); Array.Clear(block, 0, block.Length);
            read += frames;
            return frames;
        }
        private int Reject(RecordingMicrophoneFailure failure)
        { if (Failure == RecordingMicrophoneFailure.None) Failure = failure; return -1; }
        public void Close()
        {
            if (!opened) return;
            opened = false;
            try { device.End(); }
            finally { if (block != null) Array.Clear(block, 0, block.Length); block = null; anchored = recordingObserved = false; }
        }
    }
}
