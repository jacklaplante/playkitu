using System;
namespace Playkitu.Gameplay
{
    // Shared recorder clock bounds and DSP source identity. Output timestamps now
    // belong exclusively to RecordingConvertedAudio; no nominal fixed map remains.
    public static class RecordingAudioClock
    {
        public const int Rate = 48000;
        public const double MaximumUncertaintySeconds = .025;
        public const double MaximumPhaseEnvelopeSeconds = .075;
        public static bool TrySourceFrame(double anchorDsp, double firstDsp, int frames, out long sample)
        {
            sample = 0;
            if (double.IsNaN(anchorDsp) || double.IsInfinity(anchorDsp) || anchorDsp < 0 || double.IsNaN(firstDsp) || double.IsInfinity(firstDsp) || firstDsp < 0 || frames < 1 || frames > 4800) return false;
            double samples = (firstDsp - anchorDsp) * Rate;
            if (double.IsNaN(samples) || double.IsInfinity(samples) || Math.Abs(samples) > 9007199254740991d) return false;
            sample = (long)Math.Round(samples, MidpointRounding.AwayFromZero);
            return Math.Abs(samples - sample) <= .01;
        }
    }
}
