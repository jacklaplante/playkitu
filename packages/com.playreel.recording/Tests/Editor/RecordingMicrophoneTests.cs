using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Playreel.Client;

namespace Playreel.Tests
{
    public sealed class RecordingMicrophoneTests
    {
        private sealed class Device : IRecordingMicrophoneSource
        {
            public int Opens, Reads, Closes, Channels = 1, Rate = 48000, Frames = 4;
            public bool Missing, Denied, Disconnected, Repeat, ThrowRead, ThrowClose;
            public float Left = .25f, Right = -.25f;
            public float[] LastBuffer;
            public bool TryOpen(out int channels, out int rate)
            {
                Opens++; channels = Channels; rate = Rate;
                if (Denied) throw new UnauthorizedAccessException();
                return !Missing;
            }
            public int Read(float[] destination, int maximumFrames, out double firstDsp)
            {
                firstDsp = (Reads * Math.Min(Frames, maximumFrames)) / 48000d; Reads++; LastBuffer = destination;
                if (ThrowRead) throw new InvalidOperationException();
                if (Disconnected) return -1;
                if (Reads > 1 && !Repeat) return 0;
                int frames = Math.Min(Frames, maximumFrames);
                for (int i = 0; i < frames; i++) {
                    destination[i * Channels] = Left;
                    if (Channels == 2) destination[i * 2 + 1] = Right;
                }
                return frames;
            }
            public void Close() { Closes++; if (ThrowClose) throw new InvalidOperationException(); }
        }
        [Test] public void WithoutConsentDoesNoDeviceOrSampleWorkAndLeavesGameOutputUnchanged()
        {
            var device = new Device();
            using (var mic = new RecordingMicrophone(device)) {
                mic.SetConsent(false, 0); mic.Poll(1);
                var game = new[] { .1f, -.1f }; var output = new float[2]; mic.Mix(game, 1, 0, output);
                Assert.That(output, Is.EqualTo(game)); Assert.That(mic.State, Is.EqualTo(RecordingMicrophoneState.Off));
                Assert.That(device.Opens + device.Reads + device.Closes, Is.Zero);
            }
        }
        [Test] public void WithdrawalClearsOwnedPcmClosesOnceAndCannotReopenAfterDispose()
        {
            var device = new Device(); var mic = new RecordingMicrophone(device);
            mic.SetConsent(true, 0); mic.SetConsent(true, 0); mic.Poll(.1);
            Assert.That(device.Opens, Is.EqualTo(1)); Assert.That(mic.QueuedFrames, Is.EqualTo(4));
            mic.SetConsent(false, .2); mic.Poll(.3);
            Assert.That(mic.Consented, Is.False); Assert.That(mic.QueuedFrames, Is.Zero);
            Assert.That(device.LastBuffer.All(v => v == 0), Is.True);
            var queue = (float[])typeof(RecordingMicrophone).GetField("queued", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mic);
            Assert.That(queue.All(v => v == 0), Is.True, "Withdrawal must erase PCM, not only discard queue indices.");
            var output = new float[8]; mic.Mix(new float[8], 4, 0, output); Assert.That(output.All(v => v == 0), Is.True);
            int reads = device.Reads; mic.Dispose(); mic.Dispose(); mic.SetConsent(true, 1); mic.Poll(1);
            Assert.That(device.Closes, Is.EqualTo(1)); Assert.That(device.Opens, Is.EqualTo(1)); Assert.That(device.Reads, Is.EqualTo(reads));
        }
        [TestCase("missing")] [TestCase("denied")] [TestCase("channels")]
        [TestCase("rate")] [TestCase("disconnected")] [TestCase("read-throws")]
        public void DeviceFailureIsMicrophoneOnlyClearsPcmAndDoesNotAutoRetry(string failure)
        {
            var device = new Device { Missing = failure == "missing", Denied = failure == "denied",
                Channels = failure == "channels" ? 3 : 1, Rate = failure == "rate" ? 44100 : 48000, ThrowClose = true };
            using (var mic = new RecordingMicrophone(device)) {
                mic.SetConsent(true, 0);
                if (failure == "disconnected" || failure == "read-throws") {
                    mic.Poll(.1); Assert.That(mic.QueuedFrames, Is.GreaterThan(0));
                    device.Disconnected = failure == "disconnected"; device.ThrowRead = failure == "read-throws"; mic.Poll(.2);
                }
                Assert.That(mic.State, Is.EqualTo(RecordingMicrophoneState.Unavailable)); Assert.That(mic.QueuedFrames, Is.Zero);
                mic.Poll(1); mic.SetConsent(true, 1); Assert.That(device.Opens, Is.EqualTo(1)); Assert.That(device.Closes, Is.EqualTo(1));
                var game = new[] { .2f, -.2f }; var output = new float[2]; mic.Mix(game, 1, 0, output);
                Assert.That(output, Is.EqualTo(game));
            }
        }
        [TestCase(1)] [TestCase(2)] public void MonoAndStereoMixIntoSeparateRecordingBufferWithBoundedGains(int channels)
        {
            var device = new Device { Channels = channels };
            using (var mic = new RecordingMicrophone(device)) {
                mic.SetConsent(true, 0); Assert.That(mic.State, Is.EqualTo(RecordingMicrophoneState.Starting)); mic.Poll(.1);
                Assert.That(mic.State, Is.EqualTo(RecordingMicrophoneState.Active));
                mic.GameGain = .5f; mic.MicrophoneGain = 2;
                var game = new[] { .4f, .4f }; var original = (float[])game.Clone(); var output = new float[2]; mic.Mix(game, 1, 0, output);
                Assert.That(output[0], Is.EqualTo(.7f).Within(.00001));
                Assert.That(output[1], Is.EqualTo(channels == 1 ? .7f : -.3f).Within(.00001));
                Assert.That(game, Is.EqualTo(original), "Recorder mix must never become speaker monitoring.");
                Assert.Throws<ArgumentException>(() => mic.Mix(game, 1, 0, game));
                Assert.That(mic.QueuedFrames, Is.EqualTo(3));
            }
        }
        [Test] public void ReadsAndQueueStayBoundedAcrossOverrunAndWrap()
        {
            var device = new Device { Frames = RecordingMicrophone.ReadFrames, Repeat = true };
            using (var mic = new RecordingMicrophone(device)) {
                mic.SetConsent(true, 0); mic.Poll(.1); mic.Poll(.2); mic.Poll(.3);
                Assert.That(device.Reads, Is.EqualTo(15)); Assert.That(mic.QueuedFrames, Is.EqualTo(4800));
                Assert.That(mic.DroppedFrames, Is.EqualTo(10560));
                var game = new float[9600]; var output = new float[9600];
                mic.Mix(game, 4800, 10560 / 48000d, output);
                Assert.That(output.All(v => v == .25f), Is.True); Assert.That(mic.QueuedFrames, Is.Zero);
                Assert.That(device.LastBuffer.All(v => v == 0), Is.True);
                Assert.Throws<ArgumentException>(() => mic.Mix(game, 4801, 0, output));
            }
        }
        [TestCase(false)] [TestCase(true)] public void StalledStartingOrActiveDeviceBecomesUnavailableAndErasesQueue(bool active)
        {
            var device = new Device { Frames = active ? 4 : 0 };
            using (var mic = new RecordingMicrophone(device)) {
                mic.SetConsent(true, 0); mic.Poll(.1); mic.Poll(2.2);
                Assert.That(mic.State, Is.EqualTo(RecordingMicrophoneState.Unavailable)); Assert.That(mic.QueuedFrames, Is.Zero);
                Assert.That(device.Closes, Is.EqualTo(1));
                Assert.That(mic.Failure, Is.EqualTo(RecordingMicrophoneFailure.SampleStall));
            }
        }
        [TestCase(float.NaN, 1f)] [TestCase(float.PositiveInfinity, 1f)]
        [TestCase(-1f, 0f)] [TestCase(10f, 2f)]
        public void GainsAndSamplesAreFiniteAndOutputIsClipped(float gain, float expected)
        {
            using (var mic = new RecordingMicrophone(new Device { Left = float.NaN })) {
                mic.GameGain = gain; mic.MicrophoneGain = gain;
                Assert.That(mic.GameGain, Is.EqualTo(expected)); Assert.That(mic.MicrophoneGain, Is.EqualTo(expected));
                mic.SetConsent(true, 0); mic.Poll(.1); var output = new float[2];
                mic.Mix(new[] { 1f, float.NegativeInfinity }, 1, 0, output);
                Assert.That(output[0], Is.EqualTo(Math.Min(1, expected))); Assert.That(output[1], Is.Zero);
            }
        }
    }
}
