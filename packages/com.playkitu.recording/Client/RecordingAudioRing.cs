using System;
using System.Threading;

namespace Playkitu.Client
{
    // Single DSP producer / main-thread consumer. Slots are never overwritten
    // while the consumer copies to native. Callback work is bounded and allocation free.
    public sealed class RecordingAudioRing
    {
        public const int Capacity = 8, MaximumSamples = 9600;
        private readonly float[][] samples = new float[Capacity][];
        private readonly int[] lengths = new int[Capacity];
        private readonly double[] dspTimes = new double[Capacity];
        private readonly int capacity;
        private int write, read, dropped, invalid;
        public int Dropped => Volatile.Read(ref dropped);
        public int Invalid => Volatile.Read(ref invalid);
        public RecordingAudioRing(int capacity = Capacity) { if(capacity<1||capacity>Capacity)throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity=capacity; for (int i = 0; i < Capacity; i++) samples[i] = new float[MaximumSamples]; }
        public void Write(float[] data, int channels, double firstDspTime)
        {
            if (channels != 2 || data == null || data.Length == 0 || data.Length > MaximumSamples || data.Length % 2 != 0) { Interlocked.Increment(ref invalid); return; }
            int w = write;
            if (w - Volatile.Read(ref read) >= capacity) { Interlocked.Increment(ref dropped); return; }
            int slot = w % Capacity;
            Array.Copy(data, samples[slot], data.Length); lengths[slot] = data.Length; dspTimes[slot] = firstDspTime;
            Volatile.Write(ref write, w + 1);
        }
        public bool TryPeek(out float[] data, out int count, out double dspTime)
        {
            if (read == Volatile.Read(ref write)) { data = null; count = 0; dspTime = 0; return false; }
            int slot = read % Capacity; data = samples[slot]; count = lengths[slot]; dspTime = dspTimes[slot]; return true;
        }
        public void Consume() => Volatile.Write(ref read, read + 1);
    }
}
