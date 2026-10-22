using System;
using NUnit.Framework;
using Playreel.Client;
using Playreel.Gameplay;
namespace Playreel.Tests
{
    public sealed class RecordingAudioClockTests
    {
        [TestCase(0L),TestCase(-1024L),TestCase(691200000L)]
        public void OriginalSourceIdentityIncludesPreAnchorAndFourHours(long expected)
        {
            Assert.That(RecordingAudioClock.TrySourceFrame(100,100+expected/48000d,1024,out long actual),Is.True);
            Assert.That(actual,Is.EqualTo(expected));
        }
        [TestCase(double.NaN),TestCase(double.PositiveInfinity),TestCase(-1d),TestCase(100.00001)]
        public void InvalidOrOffGridSourceCannotBeConverted(double dsp)
        {Assert.That(RecordingAudioClock.TrySourceFrame(100,dsp,1024,out _),Is.False);}
        [Test]
        public void RingOverflowKeepsOriginalMissingSourceInterval()
        {
            var ring=new RecordingAudioRing();var block=new float[2048];
            for(int i=0;i<9;i++)ring.Write(block,2,100+i*1024d/48000);
            Assert.That(ring.Dropped,Is.EqualTo(1));long last=0;
            while(ring.TryPeek(out _,out int count,out double dsp)) {
                Assert.That(RecordingAudioClock.TrySourceFrame(100,dsp,count/2,out last),Is.True);ring.Consume();
            }
            ring.Write(block,2,100+9*1024d/48000);
            Assert.That(ring.TryPeek(out _,out _,out double after),Is.True);
            Assert.That(RecordingAudioClock.TrySourceFrame(100,after,1024,out long next),Is.True);
            Assert.That(next-last,Is.EqualTo(2048));
        }
    }
}
