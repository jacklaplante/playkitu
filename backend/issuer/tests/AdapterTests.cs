using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken.Model;
using NUnit.Framework;
using Playkitu.Issuer;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Issuer.Tests;

[TestFixture]
public sealed class AdapterTests
{
    [Test]
    public async Task RealSdkMarshallingAndSigningUseOnlyBlockedFakeTransport()
    {
        var transport = new RecordingTransport();
        using var sdk = new AmazonDynamoDBClient(new BasicAWSCredentials("OFFLINE", "OFFLINE"), new AmazonDynamoDBConfig
        { RegionEndpoint = RegionEndpoint.USEast2, MaxErrorRetry = 0, HttpClientFactory = new RecordingFactory(transport) });
        var store = new DynamoIssuerStore(sdk, "playkitu");
        Assert.That(await store.GetAsync<SessionState>(DbKey.Session(Guid.NewGuid()), CancellationToken.None), Is.Null);
        Assert.That(await store.CommitAsync([store.Write(new DbKey("OFFLINE"), new { value = 1 }, null)], CancellationToken.None), Is.True);
        Assert.That(transport.Requests, Has.Count.EqualTo(2));
        Assert.That(transport.Requests[0].Target, Does.EndWith(".GetItem"));
        using var get = JsonDocument.Parse(transport.Requests[0].Body);
        Assert.That(get.RootElement.GetProperty("ConsistentRead").GetBoolean(), Is.True);
        using var commit = JsonDocument.Parse(transport.Requests[1].Body);
        Assert.That(commit.RootElement.GetProperty("TransactItems")[0].GetProperty("Put").GetProperty("ConditionExpression").GetString(), Is.EqualTo("attribute_not_exists(#pk)"));
        Assert.That(transport.Requests.All(r => r.Authorization.StartsWith("AWS4-HMAC-SHA256 Credential=OFFLINE/", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task ValveCancellationDeadlineLeavesRecoverablePendingAdmission()
    {
        var f = new Fixture();
        f.Valve.Intercept = async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); };
        var watch = Stopwatch.StartNew(); var start = f.Start();
        var response = await f.Handler.HandleAsync(new APIGatewayHttpApiV2ProxyRequest { Version = "2.0", RawPath = "/v1/sessions",
            RequestContext = new() { Http = new() { Method = "POST" } }, Body = JsonSerializer.Serialize(start, Wire.Json) }, null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(response.StatusCode, Is.EqualTo(503)); Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(9)));
        Assert.That(f.Db.Control.MonthlyAdmissions, Is.Zero);
        Assert.That(f.Db.Control.Pending, Is.Empty, "Retryable verifier failure releases the fenced reservation.");
        f.Valve.Intercept = null;
        Assert.That((await f.Service.StartAsync(start with { SteamTicketHex = "ABCD" }, CancellationToken.None)).Generation, Is.EqualTo(2));
    }

    [Test]
    public async Task UnexpectedUpstreamExceptionLogsOnlyConstantDiagnostic()
    {
        var f = new Fixture(); f.Valve.Intercept = (_, _) => throw new ArithmeticException("OFFLINE-PUBLISHER-KEY ticket=PRIVATE");
        var context = new FakeContext(); var request = new APIGatewayHttpApiV2ProxyRequest { Version = "2.0", RawPath = "/v1/sessions",
            RequestContext = new() { Http = new() { Method = "POST" } }, Body = JsonSerializer.Serialize(f.Start(), Wire.Json) };
        var response = await f.Handler.HandleAsync(request, context);
        Assert.That(response.StatusCode, Is.EqualTo(503));
        Assert.That(response.Body + context.Log.Text, Does.Not.Contain("OFFLINE").And.Not.Contain("PRIVATE").And.Not.Contain("ticket="));
        Assert.That(context.Log.Text, Does.Contain("sensitive details omitted"));
        context.Remaining = TimeSpan.FromMilliseconds(100); context.Log.Text = "";
        Assert.That((await f.Handler.HandleAsync(request, context)).StatusCode, Is.EqualTo(503));
        Assert.That(context.Log.Text, Is.Empty);
    }

    [Test]
    public async Task RequestBase64BodyAndDuplicateHeadersAreHandledStrictly()
    {
        var f = new Fixture(); var start = f.Start(); var ev = new APIGatewayHttpApiV2ProxyRequest { Version = "2.0", RawPath = "/v1/sessions",
            RequestContext = new() { Http = new() { Method = "POST" } }, Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(start, Wire.Json))), IsBase64Encoded = true };
        Assert.That((await f.Handler.HandleAsync(ev, null)).StatusCode, Is.EqualTo(201));
        ev.RawPath = $"/v1/sessions/{start.SessionId:D}/authorize";
        ev.Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + start.SessionProof, ["authorization"] = "Bearer " + start.SessionProof };
        ev.Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(f.Authorize(), Wire.Json)));
        Assert.That((await f.Handler.HandleAsync(ev, null)).StatusCode, Is.EqualTo(401));
    }

    [Test]
    public async Task MonthlyDailyAndActiveTesterQuotasAreAtomic()
    {
        var f = new Fixture(); var first = f.Start(); await f.Service.StartAsync(first, CancellationToken.None);
        var next = f.Start(1);
        try { await f.Service.StartAsync(next, CancellationToken.None); Assert.Fail(); } catch (IssuerError e) { Assert.That(e.Status, Is.EqualTo(429)); }
        Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(1));
        await f.Service.CloseAsync(first.SessionId, first.SessionProof, new(1, Guid.NewGuid(), "normal"), CancellationToken.None);
        var second = f.Start(2); await f.Service.StartAsync(second, CancellationToken.None);
        await f.Service.CloseAsync(second.SessionId, second.SessionProof, new(1, Guid.NewGuid(), "normal"), CancellationToken.None);
        // C11 permits completed admissions through eight, without refund on close.
        // Keep the independent one-active-per-tester and monthly50 checks intact.
        for (int admission = 3; admission <= IssuerOptions.MaxTesterAdmissionsPerUtcDay; admission++)
        {
            var request = f.Start(admission); await f.Service.StartAsync(request, CancellationToken.None);
            await f.Service.CloseAsync(request.SessionId, request.SessionProof, new(1, Guid.NewGuid(), "normal"), CancellationToken.None);
        }
        try { await f.Service.StartAsync(f.Start(9), CancellationToken.None); Assert.Fail(); } catch (IssuerError e) { Assert.That(e.Code, Is.EqualTo("session_quota")); }
        Assert.That(f.Db.Control.DailyAdmissions, Is.EqualTo(8)); Assert.That(f.Db.Control.MonthlyAdmissions, Is.EqualTo(8));
        // Isolate monthly50 after exhausting daily8, so daily denial cannot mask it.
        f.Db.ChangeControl(g => { g.MonthlyAdmissions = 50; g.DailyAdmissions = 0; }); f.Valve.SteamId = "76561198000000002";
        try { await f.Service.StartAsync(f.Start(10), CancellationToken.None); Assert.Fail(); } catch (IssuerError e) { Assert.That(e.Status, Is.EqualTo(429)); }
    }

    [Test]
    public async Task FailedLogSigningDoesNotSuppressMediaCredentials()
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, CancellationToken.None);
        var service = new IssuerService(f.Store, new ValveVerifier(new HttpClient(f.Valve), new(f.Secrets, f.Options.PublisherSecretArn, f.Clock), f.Options.SteamAppId),
            new StsMediaIssuer(f.Sts, f.Options.MediaRoleArn), new FailingSigner(), f.Options, f.Clock);
        var result = await service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true, Fixture.Object()), CancellationToken.None);
        Assert.That(result.LogStatus, Is.EqualTo("unavailable")); Assert.That(result.Media, Is.Not.Null);
    }

    [TestCase(true, "network")]
    [TestCase(true, "timeout")]
    [TestCase(true, "stall")]
    [TestCase(false, "network")]
    [TestCase(false, "timeout")]
    [TestCase(false, "stall")]
    public async Task ProductionAdaptersContainTransportFailuresInEitherDirection(bool mediaFails, string failure)
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, CancellationToken.None);
        using var s3 = new FaultingS3();
        if (mediaFails) f.Sts.Intercept = _ => DependencyFailure<AssumeRoleResponse>(failure);
        else s3.SignerCredentials.OnResolve = () => DependencyFailure<ImmutableCredentials>(failure);
        // The healthy S3 path still uses the real SDK signer with explicit fake keys.
        var service = new IssuerService(f.Store, new ValveVerifier(new HttpClient(f.Valve), new(f.Secrets, f.Options.PublisherSecretArn, f.Clock), f.Options.SteamAppId),
            new StsMediaIssuer(f.Sts, f.Options.MediaRoleArn), new S3UploadSigner(s3, f.Options.Bucket), f.Options, f.Clock);
        var watch = Stopwatch.StartNew();
        var result = await service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true, Fixture.Object()), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)));
        Assert.That(result.MediaStatus, Is.EqualTo(mediaFails ? "unavailable" : "authorized"));
        Assert.That(result.LogStatus, Is.EqualTo(mediaFails ? "authorized" : "unavailable"));
        Assert.That(result.Uploads.Length, Is.EqualTo(mediaFails ? 1 : 0));
        Assert.That(result.Media is null, Is.EqualTo(mediaFails));
        Assert.That(f.Db.Control.Slots[0].MaxCredentialExpiry is null, Is.EqualTo(mediaFails));
        Assert.That(JsonSerializer.Serialize(result, Wire.Json), Does.Not.Contain("PRIVATE-UPSTREAM"));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ShortLambdaDeadlineLeavesTimeForHealthyPathAndFinalCommit(bool mediaFails)
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, CancellationToken.None);
        using var s3 = new FaultingS3();
        if (mediaFails) f.Sts.Intercept = _ => DependencyFailure<AssumeRoleResponse>("stall");
        else s3.SignerCredentials.OnResolve = () => DependencyFailure<ImmutableCredentials>("stall");
        var service = new IssuerService(f.Store, new ValveVerifier(new HttpClient(f.Valve), new(f.Secrets, f.Options.PublisherSecretArn, f.Clock), f.Options.SteamAppId),
            new StsMediaIssuer(f.Sts, f.Options.MediaRoleArn), new S3UploadSigner(s3, f.Options.Bucket), f.Options, f.Clock);
        var handler = new Function(service, f.Clock);
        // Exercise a shortened actual handler budget, including a slow reservation and
        // final transaction. Optional work must account for elapsed reservation time.
        f.Db.BeforeCommit = _ => Thread.Sleep(400);
        var context = new FakeContext { Remaining = TimeSpan.FromMilliseconds(3250) };
        var watch = Stopwatch.StartNew();
        var response = await handler.HandleAsync(new APIGatewayHttpApiV2ProxyRequest { Version = "2.0",
            RawPath = $"/v1/sessions/{start.SessionId:D}/authorize", RequestContext = new() { Http = new() { Method = "POST" } },
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + start.SessionProof },
            Body = JsonSerializer.Serialize(f.Authorize(true, Fixture.Object()), Wire.Json) }, context);
        Assert.That(response.StatusCode, Is.EqualTo(200), response.Body);
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(2400)));
        using var body = JsonDocument.Parse(response.Body);
        Assert.That(body.RootElement.GetProperty(mediaFails ? "logStatus" : "mediaStatus").GetString(), Is.EqualTo("authorized"));
        Assert.That(f.Db.Control.Slots[0].MaxCredentialExpiry is null, Is.EqualTo(mediaFails));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CallerCancellationIsNeverConvertedIntoPartialPublication(bool mediaStalls)
    {
        var f = new Fixture(); var start = f.Start(); await f.Service.StartAsync(start, CancellationToken.None);
        using var caller = new CancellationTokenSource(); using var s3 = new FaultingS3();
        if (mediaStalls) f.Sts.Intercept = _ => { caller.Cancel(); return DependencyFailure<AssumeRoleResponse>("stall"); };
        else s3.SignerCredentials.OnResolve = () => { caller.Cancel(); return DependencyFailure<ImmutableCredentials>("stall"); };
        var service = new IssuerService(f.Store, new ValveVerifier(new HttpClient(f.Valve), new(f.Secrets, f.Options.PublisherSecretArn, f.Clock), f.Options.SteamAppId),
            new StsMediaIssuer(f.Sts, f.Options.MediaRoleArn), new S3UploadSigner(s3, f.Options.Bucket), f.Options, f.Clock);
        var transactions = f.Db.Transactions.Count;
        try
        {
            await service.AuthorizeAsync(start.SessionId, start.SessionProof, f.Authorize(true, Fixture.Object()), caller.Token);
            Assert.Fail("Caller cancellation must propagate.");
        }
        catch (OperationCanceledException) { Assert.That(caller.IsCancellationRequested, Is.True); }
        Assert.That(f.Db.Transactions.Count, Is.EqualTo(transactions + 1), "Reservation only; no final publication.");
        Assert.That(f.Db.Control.Slots[0].MaxCredentialExpiry, Is.Null);
    }

    private static Task<T> DependencyFailure<T>(string failure) => failure switch
    {
        "network" => Task.FromException<T>(new HttpRequestException("PRIVATE-UPSTREAM transport details")),
        "timeout" => Task.FromException<T>(new TaskCanceledException("PRIVATE-UPSTREAM SDK timeout")),
        "stall" => new TaskCompletionSource<T>().Task, // Deliberately ignores cancellation, like SDK credential resolution can.
        _ => throw new ArgumentException(nameof(failure))
    };
    private sealed class FaultingCredentials : AWSCredentials
    {
        public Func<Task<ImmutableCredentials>>? OnResolve;
        public override ImmutableCredentials GetCredentials() => throw new InvalidOperationException("Offline signer must resolve credentials asynchronously.");
        public override Task<ImmutableCredentials> GetCredentialsAsync() => OnResolve is null
            ? Task.FromResult(new ImmutableCredentials("OFFLINE-SIGNER", "OFFLINE-SIGNER-SECRET", "OFFLINE-SIGNER-TOKEN")) : OnResolve();
    }
    private sealed class FaultingS3 : AmazonS3Client
    {
        public readonly FaultingCredentials SignerCredentials;
        public FaultingS3() : this(new FaultingCredentials()) { }
        private FaultingS3(FaultingCredentials credentials) : base(credentials,
            new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast2, HttpClientFactory = new BlockedFactory() })
        { SignerCredentials = credentials; }
    }
    private sealed class FailingSigner : IUploadSigner
    { public Task<UploadGrant> SignAsync(StoredObject item, DateTimeOffset expires, CancellationToken ct) => throw new IssuerError(503, "uploads_unavailable"); }
    private sealed class RecordingFactory(RecordingTransport transport) : HttpClientFactory
    { public override HttpClient CreateHttpClient(IClientConfig config) => new(transport, false); }
    private sealed class RecordingTransport : HttpMessageHandler
    {
        public List<(string Target, string Body, string Authorization)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Headers.GetValues("X-Amz-Target").Single(), await request.Content!.ReadAsStringAsync(ct), request.Headers.GetValues("Authorization").Single()));
            return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/x-amz-json-1.0") };
        }
    }
    private sealed class CapturedLog : ILambdaLogger
    {
        public string Text = "";
        public void LogLine(string message) => Text += message;
        public void Log(string message) => Text += message;
    }
    private sealed class FakeContext : ILambdaContext
    {
        public CapturedLog Log = new(); public TimeSpan Remaining = TimeSpan.FromSeconds(10);
        public string AwsRequestId => "offline";
        public IClientContext ClientContext => null!;
        public string FunctionName => "offline";
        public string FunctionVersion => "1";
        public ICognitoIdentity Identity => null!;
        public string InvokedFunctionArn => "offline";
        public ILambdaLogger Logger => Log;
        public string LogGroupName => "offline";
        public string LogStreamName => "offline";
        public int MemoryLimitInMB => 256;
        public TimeSpan RemainingTime => Remaining;
    }
}
