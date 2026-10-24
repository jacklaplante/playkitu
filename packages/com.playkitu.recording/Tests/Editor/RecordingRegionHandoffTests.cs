using System;
using System.Collections;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Playkitu.Client;
using Playkitu.Unity;

namespace Playkitu.Tests
{
    public sealed class RecordingRegionHandoffTests
    {
        private sealed class NoTickets : IRecordingTickets
        {
            public IEnumerator Begin(string identity, Func<string, IEnumerator> consumer, Action<string> failure, CancellationToken cancellation)
                => throw new InvalidOperationException("The environment lease test must not request tickets.");
        }
        [Test]
        public void ImmutableTrustRegionIsLeasedExclusivelyAndRestoredOnDependencyRelease()
        {
            string previous = Environment.GetEnvironmentVariable("PLAYREEL_AWS_REGION");
            RecordingDependencies dependencies = null;
            try {
                Environment.SetEnvironmentVariable("PLAYREEL_AWS_REGION", "ap-south-1");
                var trust = PlaykituBoundaryTests.ExampleTrust();
                var factory = new UnityRecordingFactory(trust, new NoTickets(), () => 480, Path.GetTempPath(), RecordingProfile.Standard().Freeze());
                dependencies = factory.CreateAfterConsent();
                Assert.That(Environment.GetEnvironmentVariable("PLAYREEL_AWS_REGION"), Is.EqualTo(trust.AwsRegion));
                Assert.Throws<RecordingFailure>(() => factory.CreateAfterConsent());
                Assert.That(Environment.GetEnvironmentVariable("PLAYREEL_AWS_REGION"), Is.EqualTo(trust.AwsRegion));
                dependencies.Dispose();
                Assert.That(Environment.GetEnvironmentVariable("PLAYREEL_AWS_REGION"), Is.EqualTo("ap-south-1"));
                dependencies.Dispose();
                Assert.That(Environment.GetEnvironmentVariable("PLAYREEL_AWS_REGION"), Is.EqualTo("ap-south-1"));
            } finally { dependencies?.Dispose(); Environment.SetEnvironmentVariable("PLAYREEL_AWS_REGION", previous); }
        }
    }
}
