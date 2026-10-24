using System;
using Playkitu.Native;

namespace Playkitu.Gameplay
{
    // Recorder-only post-mix seam. Immutable source frame zero is DspFrameZero,
    // paired with EpochQpc. The caller retains microphone consent/source ownership.
    public sealed class RecordingConvertedAudio : IDisposable
    {
        // true=accepted, false=explicit recorder drop (busy/backpressure). A throw
        // is fatal. Drops advance media time once and never retry or renumber PCM.
        public delegate bool Submit(float[] stereo, int sampleOffset, int frames, ulong qpc, long outputFrame);
        public const int HistoryCapacity = 64;
        public struct Mapping
        {
            public decimal OutputFrame;
            public double ProjectionSeconds, UncertaintySeconds;
        }
        private struct Interval { public long First, End; public decimal OutputFirst, Ratio; }
        private readonly Interval[] history = new Interval[HistoryCapacity];
        private int historyCount, historyHead;
        private readonly Func<IRecordingPcmConverter> factory;
        private readonly Submit submit;
        private readonly float[] output = new float[RecordingPcmConverter.MaximumOutputFrames * 2];
        private IRecordingPcmConverter converter;
        private bool disposed;
        private long sourceEnd, position;
        private decimal idealEnd, segmentIdeal;
        private long segmentProduced;
        private double previousRatio = 1;
        public ulong EpochQpc { get; }
        public ulong Frequency { get; }
        public double DspFrameZero { get; }
        public string Failure { get; private set; }
        public string FailureExceptionKind { get; private set; }
        public string FailureCode { get; private set; }
        public long ConsumedFrames { get; private set; }
        public long ProducedFrames { get; private set; }
        public long SubmittedFrames { get; private set; }
        public long RejectedFrames { get; private set; }
        public long SourceEnd => sourceEnd;
        public long OutputPosition => position;
        public decimal IdealOutputEnd => idealEnd;
        public int RetainedIntervals => historyCount;
        public decimal SourceGapEquivalentFrames { get; private set; }
        public decimal DiscardedTailEquivalentFrames { get; private set; }
        public double AppliedRatio => previousRatio;
        public RecordingConvertedAudio(ulong epochQpc, ulong frequency, Func<IRecordingPcmConverter> factory, Submit submit, double dspFrameZero = 0)
        {
            if (frequency == 0 || !Finite(dspFrameZero) || dspFrameZero < 0) throw new ArgumentOutOfRangeException();
            EpochQpc = epochQpc; Frequency = frequency; DspFrameZero = dspFrameZero;
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.submit = submit ?? throw new ArgumentNullException(nameof(submit));
        }

        public bool Convert(float[] mixedStereo, long firstSourceFrame, int frames, double ratio, int capacityFrames = RecordingPcmConverter.MaximumOutputFrames)
        {
            if (disposed) return false;
            Failure = null;
            FailureExceptionKind = FailureCode = null;
            if (firstSourceFrame < 0) { Failure = "SourceBeforeAnchor"; return false; }
            IRecordingPcmConverter pending = null;
            try {
                if (frames < 1 || frames > 4800 || mixedStereo == null || mixedStereo.Length < frames * 2
                    || !Finite(ratio) || ratio < .9998 || ratio > 1.0002 || firstSourceFrame < sourceEnd
                    || capacityFrames < 0 || capacityFrames > RecordingPcmConverter.MaximumOutputFrames) return Fail("SourceInvalid");
                bool gap = firstSourceFrame > sourceEnd;
                bool replace = converter == null || gap;
                // A gap needs a NEW converter, but do not dispose old history or
                // commit the missing interval until conversion actually succeeds.
                var active = replace ? (pending = factory()) : converter;
                if (active.LatencyInputFrames < 0 || active.LatencyInputFrames > 480) return Fail("ConverterLatency");
                if (!active.TryConvert(mixedStereo, frames, ratio, output, capacityFrames, out int produced)) {
                    Failure = "ConversionRefused"; return false; // No timeline/count/history commit.
                }
                double represented = active.AppliedRatio;
                if (produced < 0 || produced > capacityFrames || !Finite(represented) || represented < .9997999 || represented > 1.0002001)
                    return Fail("ConverterResult");
                long end = checked(firstSourceFrame + frames);
                if (replace) {
                    DiscardHistory();
                    converter = pending; pending = null; segmentIdeal = 0; segmentProduced = 0;
                }
                if (gap) {
                    // At startup no ratio was committed: use the FIRST represented
                    // command for source[0,first). Later gaps extend the last command.
                    decimal gapRatio = ConsumedFrames == 0 ? (decimal)represented : (decimal)previousRatio;
                    Append(sourceEnd, firstSourceFrame, idealEnd, gapRatio);
                    decimal missing = (firstSourceFrame - sourceEnd) * gapRatio;
                    SourceGapEquivalentFrames += missing; idealEnd += missing;
                    position = checked((long)decimal.Floor(idealEnd));
                }
                Append(firstSourceFrame, end, idealEnd, (decimal)represented);
                previousRatio = represented;
                sourceEnd = end; ConsumedFrames = checked(ConsumedFrames + frames);
                idealEnd += frames * (decimal)represented; ProducedFrames = checked(ProducedFrames + produced);
                segmentIdeal += frames * (decimal)represented; segmentProduced += produced;
                for (int offset = 0; offset < produced;) {
                    int n = Math.Min(4800, produced - offset);
                    ulong qpc = checked(EpochQpc + (ulong)decimal.Floor((decimal)position * Frequency / 48000));
                    if (submit(output, offset * 2, n, qpc, position)) SubmittedFrames = checked(SubmittedFrames + n);
                    else RejectedFrames = checked(RejectedFrames + n);
                    position = checked(position + n); offset += n;
                }
                return true;
            } catch (Exception exception) {
                // Finite diagnostics only. Never persist exception.Message/ToString:
                // factory/native exceptions can contain private paths or auth data.
                FailureExceptionKind = exception is ArgumentNullException ? "ArgumentNullException"
                    : exception is NullReferenceException ? "NullReferenceException"
                    : exception is NotSupportedException ? "NotSupportedException"
                    : exception is NotImplementedException ? "NotImplementedException"
                    : exception is System.Runtime.InteropServices.MarshalDirectiveException ? "MarshalDirectiveException"
                    : exception is DllNotFoundException ? "DllNotFoundException"
                    : exception is EntryPointNotFoundException ? "EntryPointNotFoundException"
                    : exception is InvalidOperationException ? "InvalidOperationException" : "OtherException";
                switch (exception.Message) {
                    case "resampler_platform": case "resampler_module_query": case "resampler_foreign_core": case "resampler_pin_failed":
                    case "resampler_load": case "resampler_module_mismatch": case "resampler_export": case "resampler_options":
                    case "resampler_type": case "resampler_create": case "resampler_latency": case "resampler_nonfinite": case "resampler_canary":
                        FailureCode = exception.Message; break;
                    default: FailureCode = "Unrecognized"; break;
                }
                return Fail("ConversionException");
            }
            finally { pending?.Dispose(); Array.Clear(output, 0, output.Length); }
        }

        public bool TryMapObservation(double dsp, out Mapping result, out string failure, double uncertaintyBudget = .025)
        {
            result = new Mapping(); failure = null;
            double sample = (dsp - DspFrameZero) * 48000;
            if (!Finite(sample) || Math.Abs(sample) > 9007199254740991d) { failure = "SourceInvalid"; return false; }
            // Subtracting large absolute DSP doubles can put an exact sample edge
            // just outside history/projection coverage. Normalize only sub-millionth
            // sample residue; the explicit one-frame uncertainty already covers it.
            double nearest = Math.Round(sample);
            if (Math.Abs(sample - nearest) <= .000001) sample = nearest;
            if (sample < 0) { failure = "SourceBeforeAnchor"; return false; }
            decimal source = (decimal)sample;
            // One source/output frame plus one QPC tick covers sample-grid, boundary
            // floor and rational timestamp rounding. Projection additionally allows
            // the FULL possible command spread, not just the current estimated rate.
            result.UncertaintySeconds = 1d / 48000 + 1d / Frequency;
            if (!Finite(uncertaintyBudget) || uncertaintyBudget < result.UncertaintySeconds) { failure = "MeasurementUncertain"; return false; }
            if (source == sourceEnd) { result.OutputFrame = idealEnd; return true; }
            if (source > sourceEnd) {
                double ahead = (double)(source - sourceEnd) / 48000;
                double maximumAhead = (uncertaintyBudget - result.UncertaintySeconds) / .0004002;
                result.OutputFrame = idealEnd + (source - sourceEnd) * (decimal)previousRatio;
                result.ProjectionSeconds = ahead;
                result.UncertaintySeconds += ahead * .0004002;
                if (disposed || ahead > maximumAhead) { failure = disposed ? "TimelineStopped" : "ProjectionLimit"; return false; }
                return true; // Provisional only: no history or output position changes.
            }
            for (int i = 0; i < historyCount; i++) {
                var interval = history[(historyHead + i) % HistoryCapacity];
                if (source >= interval.First && source <= interval.End) {
                    result.OutputFrame = interval.OutputFirst + (source - interval.First) * interval.Ratio;
                    return true;
                }
            }
            failure = "HistoryUnavailable"; return false;
        }
        private void Append(long first, long end, decimal outputFirst, decimal ratio)
        {
            if (historyCount == HistoryCapacity) { historyHead = (historyHead + 1) % HistoryCapacity; historyCount--; }
            history[(historyHead + historyCount++) % HistoryCapacity] = new Interval { First = first, End = end, OutputFirst = outputFirst, Ratio = ratio };
        }
        // Microphone withdrawal purges mixed filter history BEFORE another source
        // block. Do not change original source IDs, previously submitted PTS or the
        // ideal content mapping. The discarded tail becomes a real output hole.
        public void DiscardHistory()
        {
            if (converter == null) return;
            converter.Dispose(); converter = null;
            decimal tail = segmentIdeal - segmentProduced;
            if (tail < -1 || tail > 482) throw new InvalidOperationException("converter_tail_bound");
            DiscardedTailEquivalentFrames += tail;
            position = checked((long)decimal.Floor(idealEnd));
        }
        private bool Fail(string reason) { Failure = reason; Dispose(); return false; }
        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { DiscardHistory(); } finally { Array.Clear(output, 0, output.Length); }
        }
    }
}
