using System;

namespace Playreel.Client
{
    // Main-thread, nonblocking device seam. A future real adapter must defer ALL
    // enumeration/open/read until TryOpen, return available frames without waiting,
    // and stop acquisition/clear its own buffers on Close (including partial opens).
    public interface IRecordingMicrophoneSource
    {
        bool TryOpen(out int channels, out int rate);
        int Read(float[] destination, int maximumFrames, out double firstDsp); // estimated DSP interval; 0: no data, -1: lost
        void Close();
    }
    public enum RecordingMicrophoneState { Off, Starting, Active, Unavailable }
    public enum RecordingMicrophoneFailure { None, OpenFailed, FormatUnsupported, DeviceLost, InvalidSample, ClockRegressed, AmbiguousWrap, PhaseEnvelope, ReadFailed, SourceRejected, InvalidFrames, TimestampOrder, SampleStall, BackendException }
    public struct RecordingMicrophoneFacts
    {
        public double? PollGapSeconds, PhaseSeconds, BracketSeconds;
        public long? UnreadFrames;
        public long SkippedFrames;
    }
    public interface IRecordingMicrophoneDiagnostics
    {
        RecordingMicrophoneFailure Failure { get; }
        RecordingMicrophoneFacts Facts { get; }
    }

    // Independent optional-commentary boundary. Never call from the DSP callback:
    // polling/mixing stays on one owning thread, leaving the proven game-audio
    // callback and DSP/QPC mapping unchanged. No speaker monitoring or native calls.
    public sealed class RecordingMicrophone : IDisposable
    {
        public const int Rate = 48000, ReadFrames = 1024, CapacityFrames = 4800;
        public const int MaximumReadsPerPoll = (CapacityFrames + ReadFrames - 1) / ReadFrames;
        public const double StallSeconds = 2;
        private readonly IRecordingMicrophoneSource source;
        private readonly float[] incoming = new float[ReadFrames * 2];
        private readonly float[] queued = new float[CapacityFrames * 2];
        private readonly double[] dspTimes = new double[CapacityFrames];
        private int channels, head, count;
        private bool ownsSource, disposed;
        private double lastSamples, lastPoll;
        private double previousEndDsp = double.NegativeInfinity;
        private float gameGain = 1, microphoneGain = 1;
        public RecordingMicrophoneState State { get; private set; }
        public RecordingMicrophoneFailure Failure { get; private set; }
        public RecordingMicrophoneFacts Facts => (source as IRecordingMicrophoneDiagnostics)?.Facts ?? default;
        public bool Consented { get; private set; }
        public int QueuedFrames => count;
        public int DroppedFrames { get; private set; }
        public long CompletedFrames { get; private set; }
        public long MixedFrames { get; private set; }
        public float InputPeak { get; private set; }
        public double? CompletedThroughDsp => double.IsNegativeInfinity(previousEndDsp) ? (double?)null : previousEndDsp;
        public float GameGain { get => gameGain; set => gameGain = ClampGain(value); }
        public float MicrophoneGain { get => microphoneGain; set => microphoneGain = ClampGain(value); }
        public RecordingMicrophone(IRecordingMicrophoneSource source) { this.source = source ?? throw new ArgumentNullException(nameof(source)); }
        public static float ClampGain(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 1 : Math.Max(0, Math.Min(2, value));
        public static float ClampSample(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0 : Math.Max(-1, Math.Min(1, value));
        public void SetConsent(bool granted, double now)
        {
            if (!granted) { Consented = false; ClearAndClose(); State = RecordingMicrophoneState.Off; return; }
            if (disposed || Consented) return;
            Consented = true; DroppedFrames = 0; Failure = RecordingMicrophoneFailure.None;
            CompletedFrames = MixedFrames = 0; InputPeak = 0;
            previousEndDsp = double.NegativeInfinity;
            if (double.IsNaN(now) || double.IsInfinity(now)) { Unavailable(RecordingMicrophoneFailure.InvalidSample); return; }
            lastSamples = lastPoll = now;
            try {
                ownsSource = true; // Close must also clean up a failed/partial open.
                if (!source.TryOpen(out channels, out int rate)) { Unavailable(SourceFailure(RecordingMicrophoneFailure.OpenFailed)); return; }
                if ((channels != 1 && channels != 2) || rate != Rate) { Unavailable(RecordingMicrophoneFailure.FormatUnsupported); return; }
                State = RecordingMicrophoneState.Starting;
            } catch { Unavailable(RecordingMicrophoneFailure.BackendException); }
        }
        public void Poll(double now)
        {
            if (!Consented || disposed || !ownsSource) return;
            if (double.IsNaN(now) || double.IsInfinity(now) || now < lastPoll) { Unavailable(RecordingMicrophoneFailure.ClockRegressed); return; }
            lastPoll = now;
            try {
                // Five fixed reads cover the existing100ms window at48kHz; two
                // reads could not sustain ordinary main-thread cadence below23.44Hz.
                // This remains bounded, with no waiting or unbounded catch-up.
                for (int attempt = 0; attempt < MaximumReadsPerPoll; attempt++) {
                    int frames = source.Read(incoming, ReadFrames, out double firstDsp);
                    if (frames < 0) { Unavailable(SourceFailure(RecordingMicrophoneFailure.SourceRejected)); return; }
                    if (frames > ReadFrames) { Unavailable(RecordingMicrophoneFailure.InvalidFrames); return; }
                    if (frames == 0) break;
                    if (double.IsNaN(firstDsp) || double.IsInfinity(firstDsp) || firstDsp < 0 || firstDsp < previousEndDsp - .5 / Rate) { Unavailable(RecordingMicrophoneFailure.TimestampOrder); return; }
                    previousEndDsp = firstDsp + frames / (double)Rate;
                    CompletedFrames += frames;
                    lastSamples = now; State = RecordingMicrophoneState.Active;
                    // Retain newest bounded PCM, erasing old owned samples. Their
                    // original DSP intervals stay missing/silent, never time-shifted.
                    int evicted = Math.Max(0, count + frames - CapacityFrames);
                    for (int i = 0; i < evicted; i++) {
                        int slot = (head + i) % CapacityFrames; queued[slot * 2] = queued[slot * 2 + 1] = 0;
                    }
                    head = (head + evicted) % CapacityFrames; count -= evicted; DroppedFrames += evicted;
                    for (int i = 0; i < frames; i++) {
                        int target = ((head + count + i) % CapacityFrames) * 2;
                        queued[target] = ClampSample(incoming[i * channels]);
                        queued[target + 1] = ClampSample(incoming[i * channels + channels - 1]);
                        InputPeak = Math.Max(InputPeak, Math.Max(Math.Abs(queued[target]), Math.Abs(queued[target + 1])));
                        dspTimes[target / 2] = firstDsp + i / (double)Rate;
                    }
                    count += frames;
                    Array.Clear(incoming, 0, incoming.Length);
                }
                if (now - lastSamples >= StallSeconds) Unavailable(RecordingMicrophoneFailure.SampleStall);
            } catch { Unavailable(RecordingMicrophoneFailure.BackendException); }
        }
        // Mix into a separate recorder-owned destination, never the listener's live
        // speaker buffer. No resampling/re-anchoring: unsupported device rates fail
        // microphone-only. The device's fixed estimated DSP intervals select overlap;
        // the game block's DSP/QPC timestamp never changes. Missing mic means silence.
        public void Mix(float[] gameStereo, int frames, double firstDsp, float[] destination)
        {
            if (frames < 0 || frames > CapacityFrames || gameStereo == null || destination == null
                || frames * 2 > gameStereo.Length || frames * 2 > destination.Length || ReferenceEquals(gameStereo, destination))
                throw new ArgumentException("Separate bounded stereo buffers required.");
            if (double.IsNaN(firstDsp) || double.IsInfinity(firstDsp) || firstDsp < 0) throw new ArgumentException("Finite game DSP interval required.");
            for (int i = 0; i < frames; i++) {
                double target = firstDsp + i / (double)Rate;
                while (count > 0 && dspTimes[head] < target - .5 / Rate) {
                    queued[head * 2] = queued[head * 2 + 1] = 0; head = (head + 1) % CapacityFrames; count--; DroppedFrames++;
                }
                bool overlap = Consented && !disposed && count > 0 && Math.Abs(dspTimes[head] - target) <= .5 / Rate;
                int mic = head * 2;
                destination[i * 2] = ClampSample(ClampSample(gameStereo[i * 2]) * gameGain + (overlap ? queued[mic] * microphoneGain : 0));
                destination[i * 2 + 1] = ClampSample(ClampSample(gameStereo[i * 2 + 1]) * gameGain + (overlap ? queued[mic + 1] * microphoneGain : 0));
                if (overlap) { MixedFrames++; queued[mic] = queued[mic + 1] = 0; head = (head + 1) % CapacityFrames; count--; }
            }
        }
        private RecordingMicrophoneFailure SourceFailure(RecordingMicrophoneFailure fallback)
        {
            var value = (source as IRecordingMicrophoneDiagnostics)?.Failure ?? RecordingMicrophoneFailure.None;
            return value == RecordingMicrophoneFailure.None ? fallback : value;
        }
        private void Unavailable(RecordingMicrophoneFailure failure)
        { if (Failure == RecordingMicrophoneFailure.None) Failure = failure; ClearAndClose(); State = RecordingMicrophoneState.Unavailable; }
        private void ClearAndClose()
        {
            // Withdraw consent before touching a device. Clear owned PCM even when
            // the adapter throws. Already-submitted mixed native/SDK data cannot be
            // revoked by this boundary; approved previously mixed media may drain.
            Array.Clear(incoming, 0, incoming.Length); Array.Clear(queued, 0, queued.Length); head = count = 0;
            if (!ownsSource) return;
            ownsSource = false;
            try { source.Close(); } catch { /* Microphone failure cannot stop gameplay. */ }
        }
        public void Dispose() { disposed = true; SetConsent(false, 0); }
    }
}
