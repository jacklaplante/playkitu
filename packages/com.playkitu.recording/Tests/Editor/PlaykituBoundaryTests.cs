using System;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Playkitu.Client;
using Playkitu.Gameplay;
using UnityEngine;

namespace Playkitu.Tests
{
    public sealed class PlaykituBoundaryTests
    {
        [Test] public void PlayerNameIsOptionalAndAdmissionRequiresServerCapability()
        {
            var build = new RecordingBuild("test", playerDisplayName: "Fozo");
            var id = Guid.NewGuid(); var proof = RecordingIdentity.NewProof();
            var legacy = RecordingProtocol.Start(id, proof, 1800000000, "aa", build, false, DateTimeOffset.UtcNow);
            Assert.That(legacy["build"]["playerDisplayName"], Is.Null);
            var named = RecordingProtocol.Start(id, proof, 1800000000, "aa", build, false, DateTimeOffset.UtcNow, includePlayerName: true);
            Assert.That((string)named["build"]["playerDisplayName"], Is.EqualTo("Fozo"));
            Assert.That(build.Declaration()["playerDisplayName"].ToString(), Is.EqualTo("Fozo"));
            Assert.That(new RecordingBuild("legacy").Declaration()["playerDisplayName"], Is.Null);
            Assert.Throws<RecordingFailure>(() => new RecordingBuild("test", playerDisplayName: "name\ninjected"));
        }
        public static RecordingTrust ExampleTrust() => new RecordingTrust(480, "012345678901", "us-east-1",
            "https://example.execute-api.us-east-1.amazonaws.com/", "playkitu-", "example-playkitu-uploads");

        [TestCase("arn:aws:kinesisvideo:us-east-1:012345678901:stream/playkitu-session/1", true)]
        [TestCase("arn:aws:kinesisvideo:us-east-2:012345678901:stream/playkitu-session/1", false)]
        [TestCase("arn:aws:kinesisvideo:us-east-1:112345678901:stream/playkitu-session/1", false)]
        [TestCase("arn:aws:kinesisvideo:us-east-1:012345678901:stream/other-session/1", false)]
        public void StreamTrustIsBoundToExplicitDeployment(string arn, bool expected) => Assert.That(ExampleTrust().TryStream(arn, out _), Is.EqualTo(expected));

        [TestCase("https://example-playkitu-uploads.s3.us-east-1.amazonaws.com/", true)]
        [TestCase("https://other-bucket.s3.us-east-1.amazonaws.com/", false)]
        [TestCase("https://example-playkitu-uploads.s3.us-east-2.amazonaws.com/", false)]
        [TestCase("https://example-playkitu-uploads.s3.us-east-1.amazonaws.com/?redirect=1", false)]
        public void UploadDestinationIsExact(string url, bool expected) => Assert.That(ExampleTrust().IsUploadDestination(new Uri(url)), Is.EqualTo(expected));

        [Test] public void IdentityIsStableBoundedAndDomainSeparated()
        {
            var id = new Guid("11111111-2222-3333-4444-555555555555"); string proof = RecordingIdentity.NewProof();
            string identity = RecordingIdentity.Identity(id, 1800000000, proof);
            Assert.That(identity.Length, Is.EqualTo(29));
            Assert.That(identity, Is.EqualTo(RecordingIdentity.Identity(id, 1800000000, proof)));
            Assert.That(identity, Is.Not.EqualTo(RecordingIdentity.Identity(id, 1800000001, proof)));
            Assert.That(identity, Is.Not.EqualTo(RecordingIdentity.Identity(id, 1800000000, proof, domain: "other-deployment:")));
        }

        [Test] public void ComponentInstallFalseConsentStopAndRemovalAreInert()
        {
            var go = new GameObject("playkitu inert install");
            try {
                var host = go.AddComponent<PlaykituRecorder>(); var factory = new OfflineFactory();
                Assert.That(host.State, Is.EqualTo(RecordingControllerState.Consent));
                Assert.That(host.Begin(false, false, factory, RecordingProfile.Standard().Freeze(), ".", "sample"), Is.False);
                host.Stop(); Assert.That(factory.Creates, Is.Zero); Assert.That(factory.Requests, Is.Zero);
            } finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test] public void ExplicitOfflineConsentExercisesActualHostAndClosesWithoutTicketsOrDevices()
        {
            var go = new GameObject("playkitu offline lifecycle");
            var factory = new OfflineFactory();
            try {
                var host = go.AddComponent<PlaykituRecorder>();
                Assert.That(host.Begin(true, false, factory, RecordingProfile.Standard().Freeze(), ".", "sample"), Is.True);
                var update = typeof(PlaykituRecorder).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < 10 && host.State != RecordingControllerState.Finished; i++) update.Invoke(host, null);
                Assert.That(host.State, Is.EqualTo(RecordingControllerState.Finished));
                Assert.That(factory.Creates, Is.EqualTo(1)); Assert.That(factory.Requests, Is.EqualTo(1));
                Assert.That(factory.TicketCalls, Is.Zero); Assert.That(host.MicrophoneActive, Is.False);
                Assert.That(go.GetComponent<RecordingCapture>(), Is.Null);
            } finally { UnityEngine.Object.DestroyImmediate(go); }
            Assert.That(factory.Releases, Is.EqualTo(1));
        }

        // Constructed issuer-disabled response, not a recorded live session. The
        // real controller/host run; no HTTP request, native loader or device does.
        private sealed class OfflineFactory : IRecordingFactory, IRecordingTransport, IRecordingTickets
        {
            public int Creates, Requests, TicketCalls, Releases;
            public bool IsLive => false;
            public RecordingDependencies CreateAfterConsent() { Creates++; return new RecordingDependencies { Trust = ExampleTrust(), Issuer = this, Tickets = this, Runner = _ => new Runner(), Release = () => Releases++ }; }
            public IRecordingHttpOperation Begin(RecordingHttpRequest request) { Requests++; request.Validate(); return new Operation(); }
            public IEnumerator Begin(string identity, Func<string, IEnumerator> consumer, Action<string> failure, CancellationToken cancellation) { TicketCalls++; failure("ticket_unavailable"); yield break; }
        }
        private sealed class Operation : IRecordingHttpOperation
        {
            public bool IsDone => true;
            public RecordingHttpResponse Response => new RecordingHttpResponse(200, Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"serverUtc\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\",\"enabled\":false}"));
            public void Dispose() { }
        }
        private sealed class Runner : IRecordingRunner
        {
            public bool Ready => false; public bool Released => true; public string Error => null; public bool Degraded => false; public uint? NativeState => null;
            public void Begin() { } public void Tick(RecordingTiming timing, bool generating) { } public void Stop() { }
        }
    }
}
