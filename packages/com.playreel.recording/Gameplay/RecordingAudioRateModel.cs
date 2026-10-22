using System;

namespace Playreel.Gameplay
{
    // Recorder audio rate control. Capture owns source/converted PCM; observations
    // consume that same timeline and never independently integrate output time.
    // Source phase is DSP minus QPC; corrected phase is mapped output minus QPC.
    // Fit the former, feed back the conversion-owned timeline's latter. This class
    // requests future commands; only successful PCM conversion commits a ratio.
    public sealed class RecordingAudioRateModel
    {
        private const int Bins = 120, Samples = 64;
        public const double MaximumPpm = 200, SlewPpmPerSecond = 5;
        private readonly double[] samplePhase = new double[Samples], sampleResidual = new double[Samples];
        private readonly double[] sampleTime = new double[Samples];
        private readonly double[] times = new double[Bins], phases = new double[Bins], residuals = new double[Bins];
        private readonly double[] slopes = new double[Bins * (Bins - 1) / 2], scratch = new double[Bins];
        private bool anchored, fatal;
        private ulong epoch, frequency, lastQpc;
        private double dspAnchor, previousDsp, elapsed, nextSample, binStart, saturatedSince = -1;
        private double targetRatio = 1;
        private int sampleCount, binCount;
        public string Failure { get; private set; }
        public double EstimatedDspPpm { get; private set; }
        public RecordingConvertedAudio Timeline { get; }
        public double RequestedRatio { get; private set; } = 1;
        public double AppliedRatio => Timeline.AppliedRatio;
        public double PhaseSeconds { get; private set; }
        public double EnvelopeSeconds { get; private set; }
        public bool HasFit { get; private set; }
        public double FirstFitSeconds { get; private set; }
        public double MappedSeconds { get; private set; }
        public double ProjectionSeconds { get; private set; }
        public double EffectiveUncertaintySeconds { get; private set; }
        public int CompletedBins => binCount;
        public RecordingAudioRateModel(RecordingConvertedAudio timeline) { Timeline = timeline ?? throw new ArgumentNullException(nameof(timeline)); }

        public bool Observe(ulong qpc, ulong frequencyHz, ulong origin, double dsp, double uncertainty)
        {
            if (fatal) return false;
            Failure = null;
            PhaseSeconds = EnvelopeSeconds = ProjectionSeconds = double.NaN;
            EffectiveUncertaintySeconds = uncertainty;
            if (frequencyHz == 0 || qpc < origin || !Finite(dsp) || dsp < 0 || !Finite(uncertainty) || uncertainty < 0)
                return Fail("InvalidSample");
            if (frequencyHz != Timeline.Frequency) return Fail("EpochChanged");
            if (anchored && (origin != epoch || frequencyHz != frequency)) return Fail("EpochChanged");
            if (anchored && (qpc < lastQpc || dsp < previousDsp)) return Fail("ClockRegressed");
            // Retryable uncertainty must not mutate the estimator, cadence or mapping.
            if (uncertainty > RecordingAudioClock.MaximumUncertaintySeconds) { Failure = "MeasurementUncertain"; return false; }
            bool covered = Timeline.TryMapObservation(dsp, out var mapping, out string coverageFailure, RecordingAudioClock.MaximumUncertaintySeconds - uncertainty);
            if (covered || coverageFailure == "ProjectionLimit") {
                // Keep the rejected bracket's finite diagnostics too; an over-budget
                // projection is reported as rejected, never accepted as a mapping.
                MappedSeconds = (double)(mapping.OutputFrame / 48000);
                ProjectionSeconds = mapping.ProjectionSeconds; EffectiveUncertaintySeconds = uncertainty + mapping.UncertaintySeconds;
                PhaseSeconds = (double)((decimal)Timeline.EpochQpc / frequencyHz + mapping.OutputFrame / 48000 - (decimal)qpc / frequencyHz);
                EnvelopeSeconds = Math.Abs(PhaseSeconds) + EffectiveUncertaintySeconds;
            }
            if (!covered) {
                // A fresh bracket can free projection budget after preemption.
                // The capture caller retries once, then stops on repeated failure.
                if (coverageFailure == "MeasurementUncertain" || coverageFailure == "ProjectionLimit") { Failure = coverageFailure; return false; }
                return Fail(coverageFailure);
            }
            double effectiveUncertainty = uncertainty + mapping.UncertaintySeconds;
            EffectiveUncertaintySeconds = effectiveUncertainty;
            if (effectiveUncertainty > RecordingAudioClock.MaximumUncertaintySeconds) { Failure = "MeasurementUncertain"; return false; }
            if (!anchored) {
                anchored = true; epoch = origin; frequency = frequencyHz; lastQpc = qpc;
                previousDsp = dspAnchor = dsp; EnvelopeSeconds = uncertainty;
            }
            double dt = (qpc - lastQpc) / (double)frequency;
            elapsed += dt;
            lastQpc = qpc; previousDsp = dsp;
            if (EnvelopeSeconds > RecordingAudioClock.MaximumPhaseEnvelopeSeconds) return Fail("PhaseEnvelope");

            if (elapsed >= binStart + 5) {
                // Never manufacture bins across a pause in observations. A partially
                // populated old bin is timestamped from its actual retained samples.
                if (sampleCount > 0 && !CompleteBin()) return false;
                binStart = Math.Floor(elapsed / 5) * 5;
                sampleCount = 0;
            }
            if (elapsed >= nextSample && sampleCount < Samples) {
                nextSample = elapsed + .1;
                sampleTime[sampleCount] = elapsed;
                samplePhase[sampleCount] = (dsp - dspAnchor) - elapsed;
                sampleResidual[sampleCount++] = PhaseSeconds;
            }
            if (!HasFit && elapsed >= 150) return Fail("EstimateUnavailable");

            // Negative mapped-minus-observed error requires MORE output duration.
            // Slew the REQUEST only. Actual represented ratios commit at conversion
            // block boundaries; observations never run a second output clock.
            double limit = SlewPpmPerSecond * 1e-6 * dt;
            RequestedRatio += Math.Max(-limit, Math.Min(limit, targetRatio - RequestedRatio));
            if (Math.Abs(targetRatio - 1) >= MaximumPpm * 1e-6 - 1e-12) {
                if (saturatedSince < 0) saturatedSince = elapsed;
                if (elapsed - saturatedSince >= 60) return Fail("CorrectionSaturated");
            } else saturatedSince = -1;
            return true;
        }

        private int outsideFits;
        private bool CompleteBin()
        {
            Array.Sort(sampleTime, 0, sampleCount); Array.Sort(samplePhase, 0, sampleCount); Array.Sort(sampleResidual, 0, sampleCount);
            double t = Median(sampleTime, sampleCount), p = Median(samplePhase, sampleCount);
            if (binCount > 0) {
                double prediction = phases[binCount - 1] + EstimatedDspPpm * 1e-6 * (t - times[binCount - 1]);
                if (Math.Abs(p - prediction) > .020) return Fail("PhaseStep");
            }
            if (binCount == Bins) {
                Array.Copy(times, 1, times, 0, Bins - 1); Array.Copy(phases, 1, phases, 0, Bins - 1);
                Array.Copy(residuals, 1, residuals, 0, Bins - 1); binCount--;
            }
            times[binCount] = t; phases[binCount] = p;
            residuals[binCount++] = Median(sampleResidual, sampleCount);
            int n = 0;
            for (int i = 0; i < binCount; i++) for (int j = i + 1; j < binCount; j++)
                if (times[j] - times[i] >= 120 && t - times[i] <= 600)
                    slopes[n++] = (phases[j] - phases[i]) / (times[j] - times[i]);
            if (n == 0) return true; // No valid pair at the nominal 120s boundary.
            Array.Sort(slopes, 0, n); double r = Median(slopes, n);
            if (Math.Abs(r) > MaximumPpm * 1e-6) {
                if (++outsideFits >= 2) return Fail("RateUnsupported");
                return true;
            }
            outsideFits = 0;
            if (!HasFit) FirstFitSeconds = elapsed;
            HasFit = true; EstimatedDspPpm = r * 1e6;
            int recent = 0;
            for (int i = 0; i < binCount; i++) if (t - times[i] < 60) scratch[recent++] = residuals[i];
            Array.Sort(scratch, 0, recent);
            double e = Median(scratch, recent);
            // Quantized startup bias can consume all correction headroom even at
            // supported -100ppm. Limit the P contribution to75ppm so that normal
            // startup phase recovery does not falsely trip the60s saturation rule.
            double feedback = Math.Max(-75e-6, Math.Min(75e-6, -e / (120 * (1 + r))));
            targetRatio = Math.Max(1 - MaximumPpm * 1e-6, Math.Min(1 + MaximumPpm * 1e-6, 1 / (1 + r) + feedback));
            return true;
        }
        private bool Fail(string reason) { Failure = reason; fatal = true; return false; }
        private static double Median(double[] values, int n) => (values[(n - 1) / 2] + values[n / 2]) * .5;
        private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    }
}
