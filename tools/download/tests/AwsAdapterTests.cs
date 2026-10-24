using Amazon;
using Amazon.DynamoDBv2;
using Amazon.KinesisVideo;
using Amazon.KinesisVideoArchivedMedia;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SecurityToken;
using NUnit.Framework;
using Playkitu.Issuer;
using Playkitu.Retrieval;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Retrieval.Tests;

public sealed class FakeHttpFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpClientFactory
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> respond = respond;
    public List<string> Requests { get; } = [];
    public override HttpClient CreateHttpClient(IClientConfig config) => new(new Handler(this));
    private sealed class Handler(FakeHttpFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            owner.Requests.Add(request.RequestUri!.AbsolutePath);
            return owner.respond(request); // This handler has no socket/network fallback.
        }
    }
    public static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Xml(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/xml") };
}

[TestFixture]
public sealed class AwsAdapterTests
{
    private static readonly BasicAWSCredentials Fake = new("OFFLINE_ONLY", "OFFLINE_FAKE_SECRET");
    [TestCase(false)] [TestCase(true)]
    public async Task MetadataOperationMakesExactlyOneBoundedS3PageRequest(bool repeatedToken)
    {
        var prefix = new FixtureStore().State.Prefix;
        var http = new FakeHttpFactory(request => {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Assert.That(query, Does.Contain("prefix=" + prefix).And.Contain("max-keys=256").And.Contain("continuation-token=previous"));
            return Task.FromResult(FakeHttpFactory.Xml("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>true</IsTruncated><NextContinuationToken>"
                + (repeatedToken ? "previous" : "next") + "</NextContinuationToken></ListBucketResult>"));
        });
        using var d = new AmazonDynamoDBClient(Fake, RegionEndpoint.USEast2);
        using var s = new AmazonS3Client(Fake, new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = http });
        using var v = new AmazonKinesisVideoClient(Fake, RegionEndpoint.USEast2);
        var store = new AwsReadStore(d, s, v, _ => throw new AssertionException("unexpected archive factory"), Playkitu.Tests.DeploymentFixture.Create());
        if (repeatedToken) Assert.That(async () => await store.ObjectsPageAsync(prefix, 256, "previous", default), Throws.TypeOf<RetrievalError>());
        else Assert.That((await store.ObjectsPageAsync(prefix, 256, "previous", default)).NextToken, Is.EqualTo("next"));
        Assert.That(http.Requests, Has.Count.EqualTo(1));
    }
    [Test] public async Task RealDynamoMarshallerPaginatesIndexAndReusesExactStrongKeys()
    {
        var bodies = new List<JsonElement>(); var queries = 0; var fixture = new FixtureStore();
        var http = new FakeHttpFactory(async request => {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); bodies.Add(body.RootElement.Clone());
            var target = request.Headers.GetValues("X-Amz-Target").Single();
            if (target.EndsWith(".Query", StringComparison.Ordinal))
            {
                queries++;
                return queries == 1 ? FakeHttpFactory.Json(new { Items = Array.Empty<object>(), LastEvaluatedKey = new {
                    pk = new { S = DbKey.Session(FixtureStore.Id).Pk }, sk = new { S = "META" },
                    sessionDay = new { S = "2026-10-15" }, sessionOrder = new { S = "2026-10-15T00:00:00.0000000+00:00#" + FixtureStore.Id.ToString("N") } } })
                    : FakeHttpFactory.Json(new { Items = new[] { new { pk = new { S = DbKey.Session(FixtureStore.Id).Pk }, sk = new { S = "META" } } } });
            }
            var isObject = body.RootElement.GetProperty("Key").GetProperty("sk").GetProperty("S").GetString()!.StartsWith("OBJECT#", StringComparison.Ordinal);
            object state = isObject ? new StoredObject(new("log", 12, 3, Convert.ToBase64String(new byte[32])), "fixture-key", 9999999999, fixture.State.AdmissionId) : fixture.State;
            var item = new Dictionary<string, object> { ["pk"] = new { S = DbKey.Session(FixtureStore.Id).Pk }, ["sk"] = new { S = "META" },
                ["v"] = new { N = "1" }, ["data"] = new { S = JsonSerializer.Serialize(state, Wire.Json) } };
            return FakeHttpFactory.Json(new { Item = item });
        });
        using var dynamo = new AmazonDynamoDBClient(Fake, new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = http });
        using var s3 = new AmazonS3Client(Fake, new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = http });
        using var video = new AmazonKinesisVideoClient(Fake, new AmazonKinesisVideoConfig { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = http });
        var store = new AwsReadStore(dynamo, s3, video, _ => throw new AssertionException("unexpected archive factory"), Playkitu.Tests.DeploymentFixture.Create());
        Assert.That(await store.ListSessionsAsync(new(2026, 10, 15), 20, CancellationToken.None), Is.EqualTo(new[] { FixtureStore.Id }));
        Assert.That(await store.SessionAsync(FixtureStore.Id, CancellationToken.None), Is.Not.Null);
        await store.ReceiptAsync(FixtureStore.Id, "log", 12, CancellationToken.None);
        Assert.Multiple(() => {
            Assert.That(queries, Is.EqualTo(2)); Assert.That(bodies[0].GetProperty("IndexName").GetString(), Is.EqualTo("sessions-by-day"));
            Assert.That(bodies[1].GetProperty("ExclusiveStartKey").GetProperty("pk").GetProperty("S").GetString(), Is.EqualTo(DbKey.Session(FixtureStore.Id).Pk));
            Assert.That(bodies[2].GetProperty("ConsistentRead").GetBoolean(), Is.True);
            Assert.That(bodies[3].GetProperty("Key").GetProperty("sk").GetProperty("S").GetString(), Is.EqualTo("OBJECT#log#000012"));
        });
    }
    [Test] public async Task RealS3MarshallerUsesPrefixAndBoundedPagesAndExactGet()
    {
        var calls = 0; var prefix = new FixtureStore().State.Prefix;
        var http = new FakeHttpFactory(request => {
            if (request.RequestUri!.Query.Contains("list-type=2", StringComparison.Ordinal))
            {
                calls++; Assert.That(Uri.UnescapeDataString(request.RequestUri.Query), Does.Contain("prefix=" + prefix).And.Contain("max-keys=3"));
                return Task.FromResult(FakeHttpFactory.Xml("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>" + (calls == 1 ? "true" : "false")
                    + "</IsTruncated><NextContinuationToken>cursor</NextContinuationToken>" + (calls == 2 ? "<Contents><Key>" + prefix + "log/000000.gz</Key><Size>3</Size></Contents>" : "") + "</ListBucketResult>"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        });
        using var dynamo = new AmazonDynamoDBClient(Fake, RegionEndpoint.USEast2);
        using var s3 = new AmazonS3Client(Fake, new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = http });
        using var video = new AmazonKinesisVideoClient(Fake, RegionEndpoint.USEast2);
        var store = new AwsReadStore(dynamo, s3, video, _ => throw new AssertionException("unexpected archive factory"), Playkitu.Tests.DeploymentFixture.Create());
        var items = await store.ObjectsAsync(prefix, 2, CancellationToken.None); Assert.That(items.Single().Bytes, Is.EqualTo(3));
        await using var stream = await store.ObjectAsync(items.Single().Key, CancellationToken.None); using var output = new MemoryStream(); await stream.CopyToAsync(output);
        Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 })); Assert.That(calls, Is.EqualTo(2));
    }
    [Test] public async Task SessionPaginationUsesActualSdkContinuationAndRejectsAnotherDay()
    {
        var bodies = new List<JsonElement>(); var id = FixtureStore.Id;
        var key = new Dictionary<string, object> { ["pk"] = new { S = "SESSION#" + id.ToString("N") }, ["sk"] = new { S = "META" },
            ["sessionDay"] = new { S = "2026-10-15" }, ["sessionOrder"] = new { S = "2026-10-15T12:00:00.0000000+00:00#" + id.ToString("N") } };
        var http = new FakeHttpFactory(async request => {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone(); bodies.Add(body);
            return body.TryGetProperty("ExclusiveStartKey", out _) ? FakeHttpFactory.Json(new { Items = Array.Empty<object>() })
                : FakeHttpFactory.Json(new { Items = new[] { key }, LastEvaluatedKey = key });
        });
        using var d = new AmazonDynamoDBClient(Fake, new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = http });
        using var s = new AmazonS3Client(Fake, RegionEndpoint.USEast2); using var v = new AmazonKinesisVideoClient(Fake, RegionEndpoint.USEast2);
        var store = new AwsReadStore(d, s, v, _ => throw new AssertionException("unexpected archive"), Playkitu.Tests.DeploymentFixture.Create());
        var page = await store.SessionsPageAsync(new(2026, 10, 15), 50, null, default);
        Assert.That(page.Sessions, Is.EqualTo(new[] { id })); Assert.That(page.NextToken, Is.Not.Null);
        Assert.That((await store.SessionsPageAsync(new(2026, 10, 15), 50, page.NextToken, default)).NextToken, Is.Null);
        Assert.That(bodies[1].GetProperty("ExclusiveStartKey").GetProperty("pk").GetProperty("S").GetString(), Is.EqualTo("SESSION#" + id.ToString("N")));
        await Assert.ThrowsAsync<RetrievalError>(() => store.SessionsPageAsync(new(2026, 10, 16), 50, page.NextToken, default));
        Assert.That(bodies, Has.Count.EqualTo(2));
    }
    [TestCase(true)][TestCase(false)] public async Task DedicatedArnMustMatchThisAdmissionBeforeAnySdkRequest(bool valid)
    {
        var options = Playkitu.Tests.DeploymentFixture.Create(streamMode: "onDemand");
        var state = new SessionState { OnDemand = true, AdmissionId = new string('a', 32) };
        var expected = $"arn:aws:kinesisvideo:us-east-2:123456789012:stream/{options.SessionStreamName(state.AdmissionId)}/1234";
        state.StreamArn = valid ? expected : expected.Replace(state.AdmissionId, new string('b', 32));
        var http = new FakeHttpFactory(_ => Task.FromResult(FakeHttpFactory.Json(new { StreamInfo = new { StreamARN = expected } })));
        using var d = new AmazonDynamoDBClient(Fake, RegionEndpoint.USEast2); using var s = new AmazonS3Client(Fake, RegionEndpoint.USEast2);
        using var v = new AmazonKinesisVideoClient(Fake, new AmazonKinesisVideoConfig { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = http });
        var store = new AwsReadStore(d, s, v, _ => throw new AssertionException("unexpected archive"), options);
        if (valid) Assert.That(await store.SessionStreamArnAsync(state, default), Is.EqualTo(expected));
        else await Assert.ThrowsAsync<RetrievalError>(() => store.SessionStreamArnAsync(state, default));
        Assert.That(http.Requests.Count, Is.EqualTo(valid ? 1 : 0));
    }

    [Test] public async Task RealKvsMarshallerUsesProducerSelectorAndExplicitOpaqueFragmentList()
    {
        var arn = "arn:aws:kinesisvideo:us-east-2:123456789012:stream/playkitu-slot-01/1234";
        var bodyLog = new List<JsonElement>();
        var http = new FakeHttpFactory(async request => {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); bodyLog.Add(doc.RootElement.Clone());
            return request.RequestUri!.AbsolutePath switch {
                "/describeStream" => FakeHttpFactory.Json(new { StreamInfo = new { StreamARN = arn } }),
                "/getDataEndpoint" => FakeHttpFactory.Json(new { DataEndpoint = "https://fake.kinesisvideo.us-east-2.amazonaws.com" }),
                "/listFragments" => FakeHttpFactory.Json(new { Fragments = new[] { new { FragmentNumber = "opaque-fragment", FragmentLengthInMilliseconds = 1000, FragmentSizeInBytes = 3,
                    ProducerTimestamp = 1792070247m, ServerTimestamp = 1999999999m } } }),
                "/getMediaForFragmentList" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) },
                _ => throw new AssertionException("unexpected operation") };
        });
        using var d = new AmazonDynamoDBClient(Fake, RegionEndpoint.USEast2); using var s = new AmazonS3Client(Fake, RegionEndpoint.USEast2);
        using var v = new AmazonKinesisVideoClient(Fake, new AmazonKinesisVideoConfig { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = http });
        var store = new AwsReadStore(d, s, v, endpoint => new AmazonKinesisVideoArchivedMediaClient(Fake,
            new AmazonKinesisVideoArchivedMediaConfig { ServiceURL = endpoint, AuthenticationRegion = "us-east-2", MaxErrorRetry = 0, HttpClientFactory = http }), Playkitu.Tests.DeploymentFixture.Create());
        Assert.That(await store.StreamArnAsync(0, CancellationToken.None), Is.EqualTo(arn));
        var fragments = await store.FragmentsAsync(arn, TimingEvidence.Unix(FixtureStore.First), TimingEvidence.Unix(FixtureStore.First + 10000000), 10, CancellationToken.None);
        Assert.That(fragments.Single().ProducerUtc.ToUnixTimeSeconds(), Is.EqualTo(1792070247));
        await using var payload = await store.MediaAsync(arn, ["opaque-fragment"], CancellationToken.None);
        Assert.That(bodyLog.Single(b => b.TryGetProperty("FragmentSelector", out _)).GetProperty("FragmentSelector").GetProperty("FragmentSelectorType").GetString(), Is.EqualTo("PRODUCER_TIMESTAMP"));
        Assert.That(bodyLog.Single(b => b.TryGetProperty("Fragments", out _)).GetProperty("Fragments")[0].GetString(), Is.EqualTo("opaque-fragment"));
    }
    [TestCase("123456789012", "playkitu-retrieval", true)]
    [TestCase("000000000000", "playkitu-retrieval", false)]
    [TestCase("123456789012", "playkitu-deploy", false)]
    public async Task StsIdentityGuardChecksActualReturnedAccountAndRole(string account, string role, bool expected)
    {
        var http = new FakeHttpFactory(_ => Task.FromResult(FakeHttpFactory.Xml($"<GetCallerIdentityResponse xmlns=\"https://sts.amazonaws.com/doc/2011-06-15/\"><GetCallerIdentityResult><Account>{account}</Account><Arn>arn:aws:sts::{account}:assumed-role/{role}/offline-fixture</Arn><UserId>fake</UserId></GetCallerIdentityResult><ResponseMetadata><RequestId>fake</RequestId></ResponseMetadata></GetCallerIdentityResponse>")));
        using var sts = new AmazonSecurityTokenServiceClient(Fake, new AmazonSecurityTokenServiceConfig { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = http });
        if (expected) await OwnerProfile.VerifyAsync(sts, Playkitu.Tests.DeploymentFixture.Create(), CancellationToken.None);
        else Assert.That(async () => await OwnerProfile.VerifyAsync(sts, Playkitu.Tests.DeploymentFixture.Create(), CancellationToken.None), Throws.TypeOf<RetrievalError>().With.Message.EqualTo("wrong_owner_identity"));
    }
    [Test] public void NamedProfileLoaderConstructsOnlyAnExplicitFakeProcessProvider()
    {
        var testRoot = Environment.GetEnvironmentVariable("SBREC_RETRIEVAL_TEST_ROOT");
        if (string.IsNullOrEmpty(testRoot)) Assert.Ignore("Use isolated test-retrieval.ps1 for fake profile metadata.");
        var config = Environment.GetEnvironmentVariable("AWS_CONFIG_FILE")!;
        Assert.That(Path.GetFullPath(config), Is.EqualTo(Path.Combine(testRoot!, "empty-config")));
        try
        {
            // Construction only. No GetCredentials call and no executable exists:
            // a credential_process execution would fail this offline test.
            File.WriteAllText(config, "[profile retrieval-offline-only]\ncredential_process = sbrec-never-execute.exe\nregion = us-east-2\n");
            var provider = OwnerProfile.Load("retrieval-offline-only");
            Assert.That(provider, Is.TypeOf<ProcessAWSCredentials>());
            Assert.That(() => OwnerProfile.Load("absent-fixture"), Throws.TypeOf<RetrievalError>().With.Message.EqualTo("named_process_profile_required"));
        }
        finally { File.WriteAllText(config, ""); }
    }
    [TestCase(302)][TestCase(200)]
    public void HttpBoundaryRejectsRedirectOrUnboundedChunkedMetadataBeforeSdkParsing(int status)
    {
        var factory = new BoundedHttpFactory(() => new TestHandler(() => {
            var content = new StreamContent(new OwnedReadStream(new MemoryStream(new byte[1024 * 1024 + 1])));
            content.Headers.ContentType = new("application/json");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        }));
        using var client = factory.CreateHttpClient(new AmazonDynamoDBConfig());
        Assert.That(async () => await client.GetByteArrayAsync("https://offline.invalid/"), Throws.TypeOf<RetrievalError>());
    }
    private sealed class TestHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response()); }
}
