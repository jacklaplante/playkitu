using System;

namespace Playkitu.Client
{
    // Canonical numerical core shared by compile-link with owner retrieval.
    // No Unity, protocol, JSON, file or transport dependencies. This encloses one
    // supplied generated scheduling point; it does not validate its evidence,
    // interpolate media, establish a clock origin or map a decoded file.
    public static class RecordingSchedulingMath
    {
        public static bool TryInterval(long managedBeforeElapsedNs, long managedAfterElapsedNs,
            ulong correspondenceQpc, ulong qpcFrequencyHz, ulong scheduleQpc, ulong scheduleUncertaintyNs,
            out long beforeNs, out long afterNs)
        {
            beforeNs = afterNs = 0;
            if (managedBeforeElapsedNs < 0 || managedAfterElapsedNs < managedBeforeElapsedNs
                || correspondenceQpc == ulong.MaxValue || qpcFrequencyHz == 0 || qpcFrequencyHz == ulong.MaxValue
                || scheduleQpc == ulong.MaxValue || scheduleUncertaintyNs == ulong.MaxValue) return false;
            try {
                // Use decimal only as an exact integer container: |QPC delta|*1e9
                // is below1.845e28, and all integer elapsed/uncertainty additions
                // also fit its96-bit coefficient. Fractional decimal division can
                // round away a remainder near an integer; adding a large elapsed
                // origin can lose tiny fractions too. Find the exact remainder
                // first, divide an exactly divisible numerator, then use signed
                // floor/ceiling BEFORE adding the integer interval components.
                decimal numerator = ((decimal)scheduleQpc - correspondenceQpc) * 1000000000;
                decimal remainder = numerator % qpcFrequencyHz;
                decimal quotient = (numerator - remainder) / qpcFrequencyHz;
                decimal floorDelta = quotient - (remainder < 0 ? 1 : 0);
                decimal ceilingDelta = quotient + (remainder > 0 ? 1 : 0);
                decimal low = managedBeforeElapsedNs + floorDelta - scheduleUncertaintyNs;
                decimal high = managedAfterElapsedNs + ceilingDelta + scheduleUncertaintyNs;
                if (low < 0 || high > long.MaxValue) return false;
                beforeNs = (long)low; afterNs = (long)high; return true;
            } catch (OverflowException) { return false; }
        }
    }
}
