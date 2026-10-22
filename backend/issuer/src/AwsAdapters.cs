using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using System.Text.Json;

namespace Playreel.Issuer;

public sealed class PublisherKeyProvider(IAmazonSecretsManager client, string arn, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1);
    private string? cached;
    private DateTimeOffset until;
    public async Task<string> GetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null && until > clock.GetUtcNow()) return cached;
            var result = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = arn }, ct);
            // The separately provisioned secret is a plain publisher key. No config-file,
            // embedded legacy-key, local credential-store or environment-value fallback.
            if (string.IsNullOrWhiteSpace(result.SecretString) || result.SecretString.Length > 256 || result.SecretString.Any(char.IsControl))
                throw new IssuerError(503, "publisher_key_unavailable");
            cached = result.SecretString; until = clock.GetUtcNow().AddMinutes(5); return cached;
        }
        catch (Amazon.Runtime.AmazonServiceException) { throw new IssuerError(503, "publisher_key_unavailable"); }
        finally { gate.Release(); }
    }
}

public sealed class StsMediaIssuer(IAmazonSecurityTokenService client, string roleArn) : IMediaIssuer
{
    public static string Policy(string streamArn, DateTimeOffset endsAt) => JsonSerializer.Serialize(new
    {
        Version = "2012-10-17",
        Statement = new[] { new { Effect = "Allow", Action = new[] { "kinesisvideo:DescribeStream", "kinesisvideo:GetDataEndpoint", "kinesisvideo:PutMedia" },
            Resource = streamArn, Condition = new { DateLessThan = new Dictionary<string, string> { ["aws:CurrentTime"] = endsAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ") } } } }
    });
    public async Task<TemporaryMedia> IssueAsync(Guid session, string streamArn, DateTimeOffset endsAt, CancellationToken ct)
    {
        try
        {
            var response = await client.AssumeRoleAsync(new AssumeRoleRequest { RoleArn = roleArn, DurationSeconds = 900,
                RoleSessionName = "recording-" + session.ToString("N"), Policy = Policy(streamArn, endsAt) }, ct);
            var c = response.Credentials;
            if (c?.Expiration is not { } expiry) throw new IssuerError(503, "media_unavailable");
            return new(c.AccessKeyId, c.SecretAccessKey, c.SessionToken, new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)), streamArn);
        }
        catch (Exception error) when (error is Amazon.Runtime.AmazonServiceException or HttpRequestException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new IssuerError(503, "media_unavailable"); }
    }
}

public sealed class S3UploadSigner(IAmazonS3 client, string bucket) : IUploadSigner
{
    public async Task<UploadGrant> SignAsync(StoredObject item, DateTimeOffset expires, CancellationToken ct)
    {
        var descriptor = item.Descriptor;
        var fields = new Dictionary<string, string> { ["Content-Type"] = "application/gzip", ["Content-Encoding"] = "gzip",
            ["x-amz-server-side-encryption"] = "AES256", ["x-amz-checksum-algorithm"] = "SHA256", ["x-amz-checksum-sha256"] = descriptor.Sha256Base64 };
        var conditions = fields.Select(f => (S3PostCondition)S3PostCondition.ExactMatch(f.Key, f.Value)).ToList();
        conditions.Add(S3PostCondition.ContentLengthRange(descriptor.CompressedBytes, descriptor.CompressedBytes));
        try
        {
            // Signing is SDK-owned. WaitAsync bounds credential resolution too; tests supply
            // explicit fake credentials, so the SDK cannot discover local/metadata credentials.
            var result = await client.CreatePresignedPostAsync(new CreatePresignedPostRequest { BucketName = bucket, Key = item.Key,
                Expires = expires.UtcDateTime, Fields = fields, Conditions = conditions }).WaitAsync(ct);
            return new(descriptor.Kind, descriptor.Sequence, item.Key, expires, result.Url, result.Fields);
        }
        catch (Exception error) when (error is Amazon.Runtime.AmazonServiceException or HttpRequestException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new IssuerError(503, "uploads_unavailable"); }
    }
}
