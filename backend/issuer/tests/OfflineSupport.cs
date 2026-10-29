using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using System.Net;
using System.Text;
using System.Text.Json;
using Playkitu.Issuer;

namespace Issuer.Tests;

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Utc = DateTimeOffset.Parse("2026-10-14T21:00:00Z");
    public override DateTimeOffset GetUtcNow() => Utc;
    public void Advance(TimeSpan delta) => Utc += delta;
}
public sealed class BlockedFactory : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig config) => new(new BlockedHandler());
    private sealed class BlockedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Offline test blocked all network transport.");
    }
}

// This fake implements generic DynamoDB conditional-Put/transaction behavior, not a
// second admission/lease policy. Every business transition runs the production store
// and service, and the real generated expressions are evaluated before atomic writes.
public sealed class MemoryDynamo : AmazonDynamoDBClient
{
    private readonly object gate = new();
    private readonly Dictionary<(string, string), Dictionary<string, AttributeValue>> rows = [];
    public List<TransactWriteItemsRequest> Transactions { get; } = [];
    public int Reads;
    public bool ThrowAfterNextCommit;
    public Action<TransactWriteItemsRequest>? BeforeCommit;
    public MemoryDynamo() : base(new BasicAWSCredentials("OFFLINE", "OFFLINE"), new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.USEast2, HttpClientFactory = new BlockedFactory() })
    {
        var put = new DynamoIssuerStore(this, "playkitu").Write(DbKey.Control, new GlobalState(), null);
        put.Item["admissionsEnabled"] = new() { BOOL = true }; put.Item["uploadsEnabled"] = new() { BOOL = true };
        rows[("CONTROL", "META")] = Clone(put.Item);
    }
    private static Dictionary<string, AttributeValue> Clone(Dictionary<string, AttributeValue> item) =>
        item.ToDictionary(p => p.Key, p => new AttributeValue { S = p.Value.S, N = p.Value.N, BOOL = p.Value.BOOL });
    public override async Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken ct = default)
    {
        await Task.Yield(); ct.ThrowIfCancellationRequested();
        if (request.ConsistentRead != true) throw new InvalidOperationException("Authorization requires a strong read.");
        lock (gate) { Reads++; return new() { Item = rows.TryGetValue((request.Key["pk"].S, request.Key["sk"].S), out var row) ? Clone(row) : [] }; }
    }
    public override async Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken ct = default)
    {
        await Task.Yield(); ct.ThrowIfCancellationRequested(); BeforeCommit?.Invoke(request);
        lock (gate)
        {
            Transactions.Add(request);
            var puts = request.TransactItems.Where(i => i.Put is not null).Select(i => i.Put).ToArray();
            var checks = request.TransactItems.Where(i => i.ConditionCheck is not null).Select(i => new Put {
                Item = i.ConditionCheck.Key, ConditionExpression = i.ConditionCheck.ConditionExpression,
                ExpressionAttributeNames = i.ConditionCheck.ExpressionAttributeNames, ExpressionAttributeValues = i.ConditionCheck.ExpressionAttributeValues }).ToArray();
            var actions = puts.Concat(checks).ToArray();
            var identities = actions.Select(p => (p.Item["pk"].S, p.Item["sk"].S)).ToArray();
            if (actions.Length != request.TransactItems.Count || identities.Distinct().Count() != actions.Length || actions.Length > 100) throw new InvalidOperationException("Invalid Dynamo transaction.");
            foreach (var put in actions)
            {
                rows.TryGetValue((put.Item["pk"].S, put.Item["sk"].S), out var previous);
                var valid = put.ConditionExpression == "attribute_not_exists(#pk)" ? previous is null :
                    put.ConditionExpression.Split(" AND ").All(expression =>
                    {
                        var operands = expression.Split(" = ");
                        if (operands.Length != 2) throw new InvalidOperationException("Unsupported test condition: " + expression);
                        var name = put.ExpressionAttributeNames[operands[0]]; var expected = put.ExpressionAttributeValues[operands[1]];
                        return previous is not null && previous.TryGetValue(name, out var value) && value.N == expected.N && value.S == expected.S && value.BOOL == expected.BOOL;
                    });
                if (!valid) throw new TransactionCanceledException("Offline conditional conflict") { CancellationReasons = [new() { Code = "ConditionalCheckFailed" }] };
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(put.Item)) > 400000) throw new InvalidOperationException("Dynamo item budget exceeded.");
            }
            foreach (var put in puts) rows[(put.Item["pk"].S, put.Item["sk"].S)] = Clone(put.Item);
            if (ThrowAfterNextCommit)
            {
                ThrowAfterNextCommit = false;
                throw new AmazonDynamoDBException("OFFLINE sensitive exception must not escape", ErrorType.Receiver, "InternalServerError", "offline", HttpStatusCode.InternalServerError);
            }
            return new();
        }
    }
    public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.IndexName != "stream-work-by-due") throw new InvalidOperationException("Unexpected offline index.");
        lock (gate) {
            var due = long.Parse(request.ExpressionAttributeValues[":due"].N);
            return Task.FromResult(new QueryResponse { Items = rows.Values.Where(r => r.TryGetValue("workShard", out var shard)
                && shard.S == request.ExpressionAttributeValues[":shard"].S && long.Parse(r["workDue"].N) <= due)
                .OrderBy(r => long.Parse(r["workDue"].N)).Take(request.Limit!.Value).Select(Clone).ToList() });
        }
    }
    public void ChangeControl(Action<GlobalState>? change = null, bool? enabled = null)
    {
        lock (gate)
        {
            var row = rows[("CONTROL", "META")]; var data = JsonSerializer.Deserialize<GlobalState>(row["data"].S, Wire.Json)!;
            change?.Invoke(data); row["data"].S = JsonSerializer.Serialize(data, Wire.Json); row["v"].N = (long.Parse(row["v"].N) + 1).ToString();
            if (enabled is { } e) { row["admissionsEnabled"].BOOL = e; row["uploadsEnabled"].BOOL = e; }
        }
    }
    public void ChangeSession(Guid id, Action<SessionState> change)
    {
        lock (gate)
        {
            var row = rows[($"SESSION#{id:N}", "META")]; var data = JsonSerializer.Deserialize<SessionState>(row["data"].S, Wire.Json)!;
            change(data); row["data"].S = JsonSerializer.Serialize(data, Wire.Json); row["v"].N = (long.Parse(row["v"].N) + 1).ToString();
        }
    }
    public void DeleteRow(DbKey key)
    { lock (gate) rows.Remove((key.Pk, key.Sk)); }
    public GlobalState Control { get { lock (gate) return JsonSerializer.Deserialize<GlobalState>(rows[("CONTROL", "META")]["data"].S, Wire.Json)!; } }
    public int RowCount { get { lock (gate) return rows.Count; } }
    public string AllStoredText { get { lock (gate) return string.Join('\n', rows.Values.SelectMany(x => x.Values).Select(x => x.S)); } }
}
public sealed class FakeSecrets : AmazonSecretsManagerClient
{
    public int Calls;
    public string Value = "OFFLINE-PUBLISHER-KEY";
    public FakeSecrets() : base(new BasicAWSCredentials("OFFLINE", "OFFLINE"), new AmazonSecretsManagerConfig { RegionEndpoint = RegionEndpoint.USEast2, HttpClientFactory = new BlockedFactory() }) { }
    public override Task<GetSecretValueResponse> GetSecretValueAsync(GetSecretValueRequest request, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new GetSecretValueResponse { SecretString = Value }); }
}
public sealed class FakeSts(ManualClock clock) : AmazonSecurityTokenServiceClient(new BasicAWSCredentials("OFFLINE", "OFFLINE"),
    new AmazonSecurityTokenServiceConfig { RegionEndpoint = RegionEndpoint.USEast2, HttpClientFactory = new BlockedFactory() })
{
    public List<AssumeRoleRequest> Requests { get; } = [];
    public Action? DuringIssue;
    public bool Fail;
    public Func<CancellationToken, Task<AssumeRoleResponse>>? Intercept;
    public override Task<AssumeRoleResponse> AssumeRoleAsync(AssumeRoleRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Requests.Add(request); DuringIssue?.Invoke();
        if (Intercept is not null) return Intercept(ct);
        if (Fail) throw new AmazonSecurityTokenServiceException("OFFLINE STS secret exception");
        return Task.FromResult(new AssumeRoleResponse { Credentials = new Credentials { AccessKeyId = "OFFLINEMEDIAACCESS", SecretAccessKey = "OFFLINE-MEDIA-SECRET",
            SessionToken = "OFFLINE-MEDIA-TOKEN", Expiration = clock.GetUtcNow().AddMinutes(15).UtcDateTime } });
    }
}
public sealed class ValveHttp : HttpMessageHandler
{
    public List<(string Uri, string Key)> Requests { get; } = [];
    public string SteamId = "76561198000000001";
    public string AuthJson = "";
    public bool Owns = true;
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Intercept;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.GetValues("x-webapi-key").Single()));
        if (Intercept is not null) return Intercept(request, ct);
        var body = request.RequestUri.AbsolutePath.Contains("AuthenticateUserTicket")
            ? (AuthJson.Length > 0 ? AuthJson : JsonSerializer.Serialize(new { response = new { @params = new { result = "OK", steamid = SteamId } } }))
            : JsonSerializer.Serialize(new { appownership = new { ownsapp = Owns, permanent = false, timeexpires = Owns ? "never" : "2020-01-01T00:00:00Z" } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
public sealed class Fixture
{
    public ManualClock Clock = new();
    public MemoryDynamo Db = new();
    public FakeSecrets Secrets = new();
    public ValveHttp Valve = new();
    public FakeSts Sts;
    public DynamoIssuerStore Store;
    public IssuerService Service;
    public Function Handler;
    public IssuerOptions Options = Playkitu.Tests.DeploymentFixture.Create();
    public Fixture(IssuerOptions? options = null, ISessionStreams? streams = null, ISessionNotifier? notifier = null)
    {
        if (options is not null) Options = options;
        Sts = new(Clock); Store = new(Db, Options.Table);
        var s3 = new AmazonS3Client(new SessionAWSCredentials("OFFLINE-SIGNER", "OFFLINE-SIGNER-SECRET", "OFFLINE-SIGNER-TOKEN"),
            new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast2, HttpClientFactory = new BlockedFactory() });
        Service = new(Store, new ValveVerifier(new HttpClient(Valve), new(Secrets, Options.PublisherSecretArn, Clock), Options.SteamAppId),
            new StsMediaIssuer(Sts, Options.MediaRoleArn), new S3UploadSigner(s3, Options.Bucket), Options, Clock, streams, notifier);
        Handler = new(Service, Clock);
    }
    public StartRequest Start(int salt = 0) => new(1, Guid.NewGuid(), Clock.GetUtcNow().ToUnixTimeSeconds(),
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        (salt + 10).ToString("X4"), new("offline-build", "123"), new(1, true, false, Clock.GetUtcNow()));
    public AuthorizeRequest Authorize(bool media = false, params ObjectDescriptor[] objects) => new(1, Guid.NewGuid(), media, objects);
    public static ObjectDescriptor Object(int sequence = 0, long bytes = 100, string kind = "log") => new(kind, sequence, bytes, Convert.ToBase64String(new byte[32]));
}

