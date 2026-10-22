using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using NUnit.Framework;
using Playreel.Client;
using Playreel.Tests;
namespace Issuer.Tests;

[TestFixture]
public sealed class CompatibilityIdentityTests
{
    [TestCase("00112233445566778899aabbccddeeff", 1792000000L, false, "lgrec2:ZPWLfwqkgOuzyjTAKXH7rA")]
    [TestCase("00112233445566778899aabbccddeefe", 1792000000L, false, "lgrec2:TH5tmG7GumSc52LZ8ZoSJA")]
    [TestCase("00112233445566778899aabbccddeeff", 1792000001L, false, "lgrec2:9YO1Uer-dp9NGXPbk8pscA")]
    [TestCase("00112233445566778899aabbccddeeff", 1792000000L, true, "lgrec2:e52909z-inypLz5pCCRxuQ")]
    [TestCase("00112233445566778899aabbccddeeff", -1L, false, "lgrec2:Zoh_CKvnGaw5sySmtYxvUA")]
    public void NeutralCompatibilityVectorsRequireExplicitConfiguration(string id, long seconds, bool alternateProof, string expected)
    {
        // Independent SHA256 vectors generated from the published canonical ASCII
        // tuple, not from this implementation. No real deployment identifiers.
        var options = DeploymentFixture.Create(identityPrefix: "lgrec2:", identityDomain: "example-compat:");
        options.Validate();
        var proof = alternateProof ? new string('_', 42) + "8" : new string('A', 43);
        var actual = RecordingIdentity.Identity(Guid.Parse(id), seconds, proof, options.IdentityPrefix, options.IdentityDomain);
        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(actual, Is.Not.EqualTo(RecordingIdentity.Identity(Guid.Parse(id), seconds, proof)));
    }

    [Test] public async Task ConfigAndValveUseSameExplicitCompatibilityIdentity()
    {
        var f = new Fixture(DeploymentFixture.Create(identityPrefix: "lgrec2:", identityDomain: "example-compat:"));
        var request = f.Start();
        await f.Service.StartAsync(request, CancellationToken.None);
        var expected = RecordingIdentity.Identity(request.SessionId, request.AdmissionUnixSeconds, request.SessionProof, f.Options.IdentityPrefix, f.Options.IdentityDomain);
        Assert.That(f.Valve.Requests[0].Uri, Does.Contain("identity=" + Uri.EscapeDataString("str:" + expected)));
        var response = await f.Handler.HandleAsync(new APIGatewayHttpApiV2ProxyRequest { Version="2.0", RawPath="/v1/config", RequestContext=new() { Http=new() { Method="GET" } } }, null);
        using var json = JsonDocument.Parse(response.Body);
        Assert.That(json.RootElement.GetProperty("identityPrefix").GetString(), Is.EqualTo("lgrec2:"));
    }
}
