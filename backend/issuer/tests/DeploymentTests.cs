using System.Text.Json;
using NUnit.Framework;
using System.Text.Json.Nodes;
using Playkitu.Issuer;
using Playkitu.Tests;
namespace Issuer.Tests;

[TestFixture]
public sealed class DeploymentTests
{
    [TestCase(1)] [TestCase(8)]
    public async Task ConfiguredPoolSizeIsAcceptedAndMismatchedLiveLeasesFailClosed(int count)
    {
        var options = DeploymentFixture.Create(streams: count); options.Validate();
        var f = new Fixture(options);
        // The memory adapter exposes the real CONTROL marshaller/commit path.
        var control = await f.Store.GetControlAsync(default);
        control.State.Value.Slots = Enumerable.Range(0, count).Select(_ => new StreamSlot()).ToArray();
        Assert.That(await f.Store.CommitAsync([f.Store.WriteControl(control)], default), Is.True);
        Assert.That(await f.Service.ConfigAsync(default), Is.Not.Null);
        control = await f.Store.GetControlAsync(default);
        control.State.Value.Slots = Enumerable.Range(0, count == 1 ? 2 : 4).Select(_ => new StreamSlot()).ToArray();
        Assert.That(await f.Store.CommitAsync([f.Store.WriteControl(control)], default), Is.True);
        try { await f.Service.ConfigAsync(default); Assert.Fail("Mismatched pool accepted"); }
        catch (IssuerError error) { Assert.That(error.Code, Is.EqualTo("slot_configuration_mismatch")); }
    }
    [TestCase("123456789012", "us-east-2", 123456)]
    [TestCase("210987654321", "eu-west-1", 654321)]
    public void TwoIndependentDeploymentsRetainExactTrust(string account, string region, int app)
    {
        var value = DeploymentFixture.Create(account, region, app); value.Validate();
        foreach (var change in new[] {
            ("awsAccountId", "999999999999"), ("awsRegion", "us-west-2"),
            ("issuerOrigin", "https://attacker.example/"),
            ("mediaRoleArn", $"arn:aws:iam::{account}:role/Administrator"),
            ("publisherSecretArn", $"arn:aws:secretsmanager:{region}:999999999999:secret:playkitu/publisher-key-AbCd12") })
        {
            var json = JsonSerializer.SerializeToNode(value, Wire.Json)!.AsObject();
            json[change.Item1] = change.Item2;
            var bad = json.Deserialize<Deployment>(Wire.Json)!;
            Assert.Throws<InvalidOperationException>(bad.Validate, change.Item1);
        }
        var streams = JsonSerializer.SerializeToNode(value, Wire.Json)!.AsObject();
        streams["streamArns"]![0] = $"arn:aws:kinesisvideo:{region}:999999999999:stream/playkitu-slot-01/1234";
        Assert.Throws<InvalidOperationException>(streams.Deserialize<Deployment>(Wire.Json)!.Validate);
    }

    [Test] public void DefaultIdentityMatchesSharedClientAndPrivateCompatibilityIsExplicit()
    {
        var value = DeploymentFixture.Create();
        var session = Guid.Parse("11111111-2222-3333-4444-555555555555");
        const string proof = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Assert.That(Wire.Identity(session, 1700000000, proof), Is.EqualTo(Playkitu.Client.RecordingIdentity.Identity(session, 1700000000, proof, value.IdentityPrefix, value.IdentityDomain)));
        Assert.That(Wire.Identity(session, 1700000000, proof), Does.StartWith("pkrec2:"));
    }
}

