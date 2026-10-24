using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.KinesisVideo;
using Amazon.KinesisVideo.Model;
using Amazon.KinesisVideoArchivedMedia;
using Amazon.KinesisVideoArchivedMedia.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Playkitu.Issuer;

namespace Playkitu.Retrieval;

public sealed class AwsReadStore(IAmazonDynamoDB dynamo, IAmazonS3 s3, IAmazonKinesisVideo video,
    Func<string, IAmazonKinesisVideoArchivedMedia> archive, Deployment deployment) : IReadStore
{
    private string Table => deployment.Table;
    private string Bucket => deployment.Bucket;
    private readonly DynamoIssuerStore store = new(dynamo, deployment.Table);
    public async Task<SessionState?> SessionAsync(Guid id, CancellationToken ct) =>
        (await store.GetAsync<SessionState>(DbKey.Session(id), ct))?.Value;
    public async Task<StoredObject?> ReceiptAsync(Guid id, string kind, int sequence, CancellationToken ct) =>
        // DbKey.Object consumes only descriptor identity; the receipt supplies
        // the actual authorized length/checksum. Never duplicate its key format.
        (await store.GetAsync<StoredObject>(DbKey.Object(id, new(kind, sequence, 0, "")), ct))?.Value;

    public async Task<IReadOnlyList<Guid>> ListSessionsAsync(DateOnly day, int limit, CancellationToken ct)
    {
        var ids = new List<Guid>(); Dictionary<string, AttributeValue>? next = null;
        var seen = new HashSet<string>();
        for (var page = 0; page < 32; page++)
        {
            var result = await dynamo.QueryAsync(new QueryRequest { TableName = Table, IndexName = "sessions-by-day",
                KeyConditionExpression = "sessionDay = :day", ExpressionAttributeValues = new() { [":day"] = new() { S = day.ToString("yyyy-MM-dd") } },
                ProjectionExpression = "pk,sk", Limit = limit - ids.Count, ExclusiveStartKey = next, ScanIndexForward = false }, ct);
            foreach (var item in result.Items ?? [])
            {
                if (!item.TryGetValue("pk", out var pk) || !item.TryGetValue("sk", out var sk) || sk.S != "META"
                    || !pk.S.StartsWith("SESSION#", StringComparison.Ordinal) || !Guid.TryParseExact(pk.S[8..], "N", out var id))
                    throw new RetrievalError("invalid_session_index");
                if (!ids.Contains(id)) ids.Add(id);
                if (ids.Count == limit) return ids;
            }
            next = result.LastEvaluatedKey;
            if (next is null || next.Count == 0) return ids;
            var token = System.Text.Json.JsonSerializer.Serialize(next);
            if (!seen.Add(token)) throw new RetrievalError("repeated_page");
        }
        throw new RetrievalError("page_limit");
    }
    public async Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string prefix, int limit, CancellationToken ct)
    {
        var items = new List<ObjectEntry>(); string? next = null; var seen = new HashSet<string>();
        for (var page = 0; page < 32; page++)
        {
            var result = await s3.ListObjectsV2Async(new() { BucketName = Bucket, Prefix = prefix, MaxKeys = Math.Min(1000, limit + 1), ContinuationToken = next }, ct);
            foreach (var item in result.S3Objects ?? [])
            {
                if (items.Count == limit) throw new RetrievalError("object_limit");
                items.Add(new(item.Key, item.Size ?? -1));
            }
            if (result.IsTruncated != true) return items;
            next = result.NextContinuationToken;
            if (string.IsNullOrEmpty(next) || !seen.Add(next)) throw new RetrievalError("repeated_page");
        }
        throw new RetrievalError("page_limit");
    }
    public async Task<ObjectPage> ObjectsPageAsync(string prefix, int limit, string? continuation, CancellationToken ct)
    {
        if (limit is < 1 or > 256 || continuation?.Length > 8192) throw new RetrievalError("invalid_metadata_page");
        var response = await s3.ListObjectsV2Async(new() { BucketName = Bucket, Prefix = prefix, MaxKeys = limit,
            ContinuationToken = continuation }, ct);
        var entries = (response.S3Objects ?? []).Select(o => new ObjectEntry(o.Key, o.Size ?? -1)).ToArray();
        var next = response.IsTruncated == true ? response.NextContinuationToken : null;
        if (entries.Length > limit || response.IsTruncated == true && (string.IsNullOrEmpty(next) || next == continuation || next.Length > 8192))
            throw new RetrievalError("invalid_metadata_page");
        return new(entries, next);
    }
    public async Task<Stream> ObjectAsync(string key, CancellationToken ct)
    {
        var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = key }, ct);
        return new OwnedReadStream(response.ResponseStream, response);
    }
    public async Task<string> StreamArnAsync(int slot, CancellationToken ct)
    {
        if (slot < 0 || slot >= deployment.StreamArns.Length) throw new RetrievalError("invalid_slot");
        var expected = deployment.StreamArns[slot];
        var name = expected.Split('/')[1];
        var response = await video.DescribeStreamAsync(new() { StreamName = name }, ct);
        var arn = response.StreamInfo?.StreamARN;
        if (arn != expected) throw new RetrievalError("wrong_stream");
        return arn;
    }
    private async Task<string> EndpointAsync(string arn, APIName api, CancellationToken ct)
    {
        var result = await video.GetDataEndpointAsync(new() { StreamARN = arn, APIName = api }, ct);
        if (!Uri.TryCreate(result.DataEndpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"
            || !uri.Host.EndsWith(".kinesisvideo." + deployment.AwsRegion + ".amazonaws.com", StringComparison.OrdinalIgnoreCase))
            throw new RetrievalError("invalid_data_endpoint");
        return uri.AbsoluteUri;
    }
    public async Task<IReadOnlyList<FragmentEntry>> FragmentsAsync(string arn, DateTimeOffset start, DateTimeOffset end, int limit, CancellationToken ct)
    {
        using var client = archive(await EndpointAsync(arn, APIName.LIST_FRAGMENTS, ct));
        var entries = new List<FragmentEntry>(); string? next = null; var seen = new HashSet<string>();
        for (var page = 0; page < 32; page++)
        {
            var response = await client.ListFragmentsAsync(new() { StreamARN = arn, MaxResults = Math.Min(1000, limit + 1), NextToken = next,
                FragmentSelector = next is null ? new() { FragmentSelectorType = FragmentSelectorType.PRODUCER_TIMESTAMP,
                    TimestampRange = new() { StartTimestamp = start.UtcDateTime, EndTimestamp = end.UtcDateTime } } : null }, ct);
            foreach (var f in response.Fragments ?? [])
            {
                if (entries.Count == limit) throw new RetrievalError("fragment_limit");
                if (string.IsNullOrEmpty(f.FragmentNumber) || f.FragmentNumber.Length > 128 || f.FragmentNumber.Any(c => c < 33 || c > 126)
                    || f.ProducerTimestamp is null || f.FragmentLengthInMilliseconds is null || f.FragmentSizeInBytes is null)
                    throw new RetrievalError("invalid_fragment_metadata");
                entries.Add(new(f.FragmentNumber, new(DateTime.SpecifyKind(f.ProducerTimestamp.Value, DateTimeKind.Utc)), f.FragmentLengthInMilliseconds.Value, f.FragmentSizeInBytes.Value));
            }
            next = response.NextToken;
            if (string.IsNullOrEmpty(next)) return entries;
            if (!seen.Add(next)) throw new RetrievalError("repeated_page");
        }
        throw new RetrievalError("page_limit");
    }
    public async Task<Stream> MediaAsync(string arn, IReadOnlyList<string> fragments, CancellationToken ct)
    {
        var client = archive(await EndpointAsync(arn, APIName.GET_MEDIA_FOR_FRAGMENT_LIST, ct));
        try
        {
            var response = await client.GetMediaForFragmentListAsync(new() { StreamARN = arn, Fragments = fragments.ToList() }, ct);
            return new OwnedReadStream(response.Payload, response, client);
        }
        catch { client.Dispose(); throw; }
    }
}

public sealed class OwnedReadStream(Stream inner, params IDisposable[] owners) : Stream
{
    public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); foreach (var owner in owners) owner.Dispose(); } base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public static class OwnerProfile
{
    // This factory is reached only by explicit list/fetch. Local inspect/map and
    // offline adapter tests use only a non-executed fake profile. The SDK, not
    // our tool, owns credential_process output.
    public static AWSCredentials Load(string name)
    {
        string RequiredPath(string key)
        {
            var path = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new RetrievalError("explicit_profile_paths_required");
            return path;
        }
        _ = RequiredPath("AWS_CONFIG_FILE");
        var credentialsFile = RequiredPath("AWS_SHARED_CREDENTIALS_FILE");
        var cache = Environment.GetEnvironmentVariable("AWS_LOGIN_CACHE_DIRECTORY");
        if (string.IsNullOrEmpty(cache) || !Path.IsPathFullyQualified(cache) || !Directory.Exists(cache) || string.IsNullOrWhiteSpace(name))
            throw new RetrievalError("explicit_profile_paths_required");
        var chain = new CredentialProfileStoreChain(credentialsFile);
        if (!chain.TryGetProfile(name, out var profile) || string.IsNullOrWhiteSpace(profile.Options.CredentialProcess)
            || !string.IsNullOrEmpty(profile.Options.AccessKey) || !string.IsNullOrEmpty(profile.Options.SecretKey) || !string.IsNullOrEmpty(profile.Options.Token)
            || !chain.TryGetAWSCredentials(name, out var credentials)) throw new RetrievalError("named_process_profile_required");
        return credentials;
    }
    public static async Task VerifyAsync(IAmazonSecurityTokenService sts, Deployment deployment, CancellationToken ct)
    {
        var result = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
        if (result.Account != deployment.AwsAccountId || result.Arn is null
            || !System.Text.RegularExpressions.Regex.IsMatch(result.Arn, "\\Aarn:aws:sts::" + deployment.AwsAccountId + ":assumed-role/" + System.Text.RegularExpressions.Regex.Escape(deployment.Project + "-retrieval") + "/[A-Za-z0-9_+=,.@-]+\\z"))
            throw new RetrievalError("wrong_owner_identity");
    }
}

